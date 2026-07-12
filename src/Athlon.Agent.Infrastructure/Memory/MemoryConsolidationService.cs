using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Compaction;
using System.Diagnostics;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Periodically merges daily ledgers into a curated, deduplicated, size-bounded MEMORY.md.
/// Uses a watermark (.consolidation_state) to process only new daily files.
/// </summary>
public sealed class MemoryConsolidationService(
    ILongTermMemory longTermMemory,
    IAgentModelClient modelClient,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IFileStorageService storage,
    IActiveAgentSessionContext activeSessionContext,
    AppSettings settings,
    IAppLogger logger)
{
    private const string ConsolidationPrompt = """
You are a memory consolidation assistant. You own the curated long-term memory file MEMORY.md. Your job is to merge new daily ledger entries into MEMORY.md while keeping it concise, deduplicated, and high-signal.

You are given two inputs:
1. The current MEMORY.md content (the existing curated long-term memory).
2. New daily ledger entries that have been appended since the last consolidation.

Rules:
- MEMORY.md is the single source of truth for cross-day, cross-session knowledge. Keep it stable and authoritative.
- Daily ledger entries are stream-of-consciousness flush logs — they may be noisy, redundant with MEMORY.md, or redundant with each other. Promote only what is durable and reusable.
- Deduplicate: if a new entry restates something MEMORY.md already covers, skip it.
- Merge related facts: combine entries about the same topic into cohesive paragraphs with clear section headers.
- Update or remove stale information when new entries supersede it.
- Keep total output within {0} tokens (approximately {1} characters); prioritize recent and frequently-referenced information when trimming.

Output the COMPLETE new MEMORY.md content (not just a diff). Use markdown.
""";

    private readonly IAppLogger _logger = logger.ForContext("MemoryConsolidationService");
    private readonly MemorySettings _cfg = settings.Memory;

    /// <summary>Runs a single consolidation cycle. No-op if no new daily files exist.</summary>
    public async Task ConsolidateAsync(CancellationToken cancellationToken = default)
    {
        var watermark = await longTermMemory.ReadWatermarkAsync(cancellationToken);
        if (watermark == default)
            watermark = DateTime.MinValue;

        var dailyFiles = await longTermMemory.ListDailyFilesAfterAsync(watermark, cancellationToken);
        if (dailyFiles.Count == 0)
        {
            _logger.Debug("No fresh daily entries since {Watermark:O} — skipping consolidation", watermark);
            return;
        }

        var runStart = DateTime.UtcNow;
        var currentMemory = await longTermMemory.ReadCuratedAsync(cancellationToken);
        var dailyEntries = await ReadDailyEntriesAsync(dailyFiles, cancellationToken);

        var maxChars = ContextTokenEstimator.EstimateCharacterBudget(_cfg.MaxMemoryTokens);
        var systemPrompt = string.Format(ConsolidationPrompt, _cfg.MaxMemoryTokens, maxChars);

        var userContent = new StringBuilder();
        userContent.AppendLine("Current MEMORY.md:");
        userContent.AppendLine(string.IsNullOrWhiteSpace(currentMemory) ? "(empty)" : currentMemory);
        userContent.AppendLine();
        userContent.AppendLine("New daily ledger entries to merge" +
            (watermark > DateTime.MinValue ? $" (since {watermark:O})" : "") + ":");
        userContent.AppendLine();
        userContent.Append(dailyEntries);

        var request = new AgentModelRequest(
            new[]
            {
                new AgentModelMessage("system", systemPrompt),
                new AgentModelMessage("user", userContent.ToString())
            },
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: _cfg.SummaryMaxTokens);

        AgentModelResponse response;
        var sessionId = activeSessionContext.SessionId ?? "memory-consolidation";
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
                    settings.Model.ModelName, promptTokens, 0,
                    "failure", ex.GetType().Name, stopwatch.ElapsedMilliseconds),
                CancellationToken.None).ConfigureAwait(false);
            _logger.Warning("Memory consolidation LLM call failed: {Error}", ex.Message);
            return;
        }

        var consolidated = response.Content?.Trim();
        if (string.IsNullOrWhiteSpace(consolidated))
        {
            _logger.Warning("Consolidation produced empty output, skipping");
            return;
        }

        consolidated = EnforceMaxLength(consolidated, maxChars);
        await longTermMemory.WriteCuratedAsync(consolidated, cancellationToken);
        await longTermMemory.WriteWatermarkAsync(runStart, cancellationToken);
        _logger.Information("MEMORY.md consolidated ({Length} chars), watermark advanced to {Watermark:O}",
            consolidated.Length, runStart);
    }

    private async Task<string> ReadDailyEntriesAsync(
        IReadOnlyList<string> fileNames,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        foreach (var name in fileNames)
        {
            var content = await longTermMemory.ReadDailyFileAsync(name, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine("### " + name);
                sb.AppendLine(content.Trim());
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public static string EnforceMaxLength(string content, int maxChars)
    {
        if (content.Length <= maxChars)
        {
            return content;
        }

        return content[..maxChars] + "\n\n...(truncated — use memory_get)...";
    }
}
