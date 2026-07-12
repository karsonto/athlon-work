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
    private const string FlushSystemPrompt = """
You are a memory extraction assistant. Analyze the conversation below and extract important facts, decisions, preferences, and contextual information that should be remembered for future conversations.

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

    private readonly IAppLogger _logger = logger.ForContext("MemoryFlushService");
    private readonly MemorySettings _cfg = settings.Memory;

    public async Task<MemoryFlushResult> FlushAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var conversationText = SerializeMessages(messages);
        if (string.IsNullOrWhiteSpace(conversationText))
            return MemoryFlushResult.Skipped;

        var existingMemory = await longTermMemory.ReadCuratedAsync(cancellationToken);
        var today = DateTime.UtcNow;
        var existingDaily = await longTermMemory.ReadDailyAsync(today, cancellationToken);

        var userPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingMemory))
        {
            userPrompt.AppendLine("MEMORY.md (read-only curated long-term memory — do NOT restate):");
            userPrompt.AppendLine(existingMemory);
            userPrompt.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(existingDaily))
        {
            userPrompt.AppendLine("Today's daily ledger so far (your output will be appended after):");
            userPrompt.AppendLine(existingDaily);
            userPrompt.AppendLine();
        }
        userPrompt.AppendLine("Extract NEW memories from this conversation window (skip anything already covered above):");
        userPrompt.AppendLine();
        userPrompt.Append(conversationText);

        var request = new AgentModelRequest(
            new[]
            {
                new AgentModelMessage("system", FlushSystemPrompt),
                new AgentModelMessage("user", userPrompt.ToString())
            },
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: _cfg.SummaryMaxTokens);

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

        var dailyEntry = $"\n## Memory Flush — {DateTime.UtcNow:O}\n{extracted}\n";
        await longTermMemory.AppendDailyAsync(dailyEntry, cancellationToken);
        _logger.Information("Flushed {Length} chars to daily memory ledger", extracted.Length);
        return MemoryFlushResult.Success(extracted);
    }

    private static string SerializeMessages(IReadOnlyList<ChatMessage> messages)
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
        if (result.Length > 80_000)
            result = result[^80_000..];
        return result;
    }
}
