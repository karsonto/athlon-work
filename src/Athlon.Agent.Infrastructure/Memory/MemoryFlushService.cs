using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Compaction;
using System.Diagnostics;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Extracts new long-term memories from a finished conversation turn via LLM,
/// then appends them to today's daily memory ledger.
/// </summary>
public sealed class MemoryFlushService(
    ILongTermMemory longTermMemory,
    IAgentModelClient modelClient,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IFileStorageService storage,
    IActiveAgentSessionContext activeSessionContext,
    AppSettings settings,
    IAppLogger logger)
{
    internal const string FlushGuidelines = """
Output ONLY the extracted memories as a markdown bullet list. Each item should be a concise, self-contained fact. Include dates, names, and specifics when available.

If there is nothing worth remembering, respond with exactly: NO_REPLY

Guidelines:
- Extract user preferences, personal information, project decisions
- Capture important technical decisions and their rationale
- Note any commitments, deadlines, or action items
- Ignore routine greetings, tool invocations, and ephemeral status updates

IMPORTANT:
- You are writing to TODAY's daily memory ledger (memory/YYYY-MM-DD.md), NOT to MEMORY.md.
- MEMORY.md is the curated long-term memory and is shown ONLY as read-only context below. Do NOT restate facts already covered by MEMORY.md or by today's earlier entries.
- Keep each bullet point independent and self-contained.
""";

    internal static readonly string FlushSystemPrompt =
        "You are a memory extraction assistant. Analyze the conversation below and extract important facts, decisions, preferences, and contextual information that should be remembered for future conversations.\n\n" + FlushGuidelines;

    internal static readonly string FlushInstruction =
        "Analyze the preceding conversation and extract important facts, decisions, preferences, and contextual information that should be remembered for future conversations.\n\n" + FlushGuidelines + "\n- The conversation to extract from is in the preceding messages.";

    private readonly IAppLogger _logger = logger.ForContext("MemoryFlushService");
    private readonly MemorySettings _cfg = settings.Memory;

    public async Task<MemoryFlushResult> FlushAsync(
        MemoryTurnContext context,
        CancellationToken cancellationToken = default)
    {
        if (!HasExtractableConversation(context.Messages))
        {
            return MemoryFlushResult.Skipped;
        }

        var existingMemory = await longTermMemory.ReadCuratedAsync(cancellationToken);
        var today = DateTime.UtcNow;
        var existingDaily = await longTermMemory.ReadDailyAsync(today, cancellationToken);
        var request = BuildRequest(context, existingMemory, existingDaily);

        AgentModelResponse response;
        var sessionId = activeSessionContext.SessionId ?? "memory-flush";
        var attemptId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            response = await modelClient.CompleteAsync(request, cancellationToken: cancellationToken);
            var usage = ModelUsageAccounting.Resolve(request, response);
            stopwatch.Stop();
            sessionUsageAccumulator.RecordCall(sessionId, attemptId, ModelCallPurpose.Memory, usage);
            await storage.AppendAttemptEventAsync(
                sessionId,
                new AgentAttemptEvent(
                    DateTimeOffset.UtcNow, attemptId, sessionId, sessionId, AgentAttemptKind.Model,
                    ModelCallPurpose.Memory, null, ToolCatalogFingerprint.Compute(request.Tools),
                    settings.Model.ModelName, usage.PromptTokens ?? 0, usage.CompletionTokens ?? 0,
                    "success", null, stopwatch.ElapsedMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var promptTokens = ContextTokenEstimator.EstimateModelRequest(request);
            sessionUsageAccumulator.RecordCall(
                sessionId, attemptId, ModelCallPurpose.Memory, new ModelUsage(promptTokens, 0, promptTokens));
            await storage.AppendAttemptEventAsync(
                sessionId,
                new AgentAttemptEvent(
                    DateTimeOffset.UtcNow, attemptId, sessionId, sessionId, AgentAttemptKind.Model,
                    ModelCallPurpose.Memory, null, ToolCatalogFingerprint.Compute(request.Tools),
                    settings.Model.ModelName, promptTokens, 0, "failure", ex.GetType().Name,
                    stopwatch.ElapsedMilliseconds),
                CancellationToken.None).ConfigureAwait(false);
            _logger.Warning("Memory flush LLM call failed: {Error}", ex.Message);
            return MemoryFlushResult.Failed(ex.Message);
        }

        var extracted = response.Content?.Trim();
        if (string.IsNullOrWhiteSpace(extracted) || extracted == "NO_REPLY")
        {
            _logger.Debug("No memories to flush");
            return MemoryFlushResult.Skipped;
        }

        var dailyEntry = $"\n## Memory Flush — {AppTimeZone.Now:O}\n{extracted}\n";
        await longTermMemory.AppendDailyAsync(dailyEntry, cancellationToken);
        _logger.Information("Flushed {Length} chars to daily memory ledger", extracted.Length);
        return MemoryFlushResult.Success(extracted);
    }

    private AgentModelRequest BuildRequest(
        MemoryTurnContext context,
        string? existingMemory,
        string? existingDaily)
    {
        var ledger = BuildLedgerAppendix(existingMemory, existingDaily);
        if (!string.IsNullOrWhiteSpace(context.EnvironmentPrompt))
        {
            var built = ModelMessagesForApiBuilder.Build(
                cache: null,
                context.EnvironmentPrompt,
                context.Messages,
                settings.ContextCompaction);
            var messages = built.Messages.ToList();
            messages.Add(new AgentModelMessage("user", FlushInstruction + ledger));
            return new AgentModelRequest(
                messages,
                context.Tools ?? Array.Empty<ToolDefinition>(),
                AllowToolCalls: false,
                MaxTokens: _cfg.SummaryMaxTokens);
        }

        var conversationText = SerializeMessages(context.Messages);
        return new AgentModelRequest(
            [
                new AgentModelMessage("system", FlushSystemPrompt),
                new AgentModelMessage("user", "Extract NEW memories from this conversation window (skip anything already covered above):" + ledger + "\n\n" + conversationText)
            ],
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: _cfg.SummaryMaxTokens);
    }

    private static string BuildLedgerAppendix(string? existingMemory, string? existingDaily)
    {
        var userPrompt = new StringBuilder();
        userPrompt.AppendLine();
        if (!string.IsNullOrWhiteSpace(existingMemory))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("MEMORY.md (read-only curated long-term memory — do NOT restate):");
            userPrompt.AppendLine(existingMemory);
        }

        if (!string.IsNullOrWhiteSpace(existingDaily))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("Today's daily ledger so far (your output will be appended after):");
            userPrompt.AppendLine(existingDaily);
        }

        return userPrompt.ToString();
    }

    internal static bool HasExtractableConversation(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role is MessageRole.System or MessageRole.Compaction)
            {
                continue;
            }

            if (message.Role == MessageRole.User && message.Content?.Contains("<session_context>") == true)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                return true;
            }
        }

        return false;
    }

    private string SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.Role is MessageRole.System or MessageRole.Compaction)
                continue;
            if (message.Role == MessageRole.User && message.Content?.Contains("<session_context>") == true)
                continue;

            sb.Append('[').Append(message.Role).Append("]: ");
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }

        var result = sb.ToString();
        var maxChars = Math.Max(1, _cfg.MaxFlushConversationChars);
        if (result.Length > maxChars)
        {
            result = result[^maxChars..];
        }

        return result;
    }
}
