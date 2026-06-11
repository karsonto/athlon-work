using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure.Compaction;

namespace Athlon.Agent.Infrastructure;

public sealed class ConversationCompactor(
    AppSettings settings,
    IAgentModelClient modelClient,
    IFileStorageService storage,
    TruncateArgsService truncateArgsService,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IAppLogger logger) : IConversationCompactor
{
    private readonly IAppLogger _logger = logger.ForContext("ConversationCompactor");

    public async Task<ConversationCompactResult> CompactIfNeededAsync(
        AgentSession session,
        CompactionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var cfg = settings.ContextCompaction;
        var conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
        if (conversation.Count == 0)
        {
            return new ConversationCompactResult(session, false);
        }

        var truncateArgsApplied = request.Plan?.ApplyTruncateArgs == true;
        if (!cfg.DynamicCompaction.Enabled)
        {
            if (!truncateArgsApplied)
            {
                conversation = ConversationMessageFilters.WithoutCompactionAudits(
                    truncateArgsService.ApplyToMessages(conversation, cfg, out truncateArgsApplied));
            }
        }
        else if (truncateArgsApplied)
        {
            // truncate already applied in dynamic pipeline.
        }

        var estimatedTokens = ContextTokenEstimator.ResolveEffectiveEstimate(
            conversation,
            cfg,
            request.RuntimeContext?.Budget);
        var shouldCompact = request.RuntimeContext is { } runtime && cfg.DynamicCompaction.Enabled
            ? ContextPressureEvaluator.ShouldCompact(
                runtime.Budget,
                conversation,
                cfg,
                request.Plan?.Pressure ?? ContextPressureLevel.Normal,
                request.Force)
            : ConversationCutoffPlanner.ShouldCompact(conversation, estimatedTokens, cfg, request.Force);

        if (!shouldCompact)
        {
            return new ConversationCompactResult(session, false);
        }

        var keepTokenBudget = request.Plan?.KeepTokenBudget;
        var cutoff = ConversationCutoffPlanner.DetermineCutoffIndex(
            conversation,
            estimatedTokens,
            cfg,
            keepTokenBudget);
        if (cutoff <= 0)
        {
            _logger.Debug("Compaction triggered but safe cutoff is 0 — skipping");
            return new ConversationCompactResult(session, false);
        }

        var prefix = SummaryMessageBuilder.FilterSummaryMessages(conversation.Take(cutoff).ToList());
        var tail = conversation.Skip(cutoff).ToList();
        var originalCount = conversation.Count;
        var tokensBefore = estimatedTokens;

        string? transcriptPath = null;
        if (cfg.OffloadBeforeCompact)
        {
            transcriptPath = await storage.SaveTranscriptAsync(session.Id, session.Messages, cancellationToken);
        }

        var formatted = ConversationSummaryFormatter.FormatMessages(prefix);
        int? summaryInputCharsBefore = formatted.Length;
        int? summaryInputCharsAfter = formatted.Length;
        int? hygieneSavingsEstimate = null;
        var calibrationMultiplier = request.RuntimeContext?.CalibrationMultiplier ?? 1.0;
        if (formatted.Length > cfg.MaxConversationCharsForSummary
            || ContextTokenEstimator.EstimateTextTokens(formatted, calibrationMultiplier) > cfg.RequestHistoryHygiene.MaxToolResultTokens)
        {
            var compacted = RequestHistoryHygiene.CompactTextForSummary(formatted, cfg.RequestHistoryHygiene);
            formatted = compacted.Text;
            summaryInputCharsBefore = compacted.CharsBefore;
            summaryInputCharsAfter = compacted.CharsAfter;
            hygieneSavingsEstimate = compacted.EstimatedSavingsTokens;
        }

        if (formatted.Length > cfg.MaxConversationCharsForSummary)
        {
            formatted = formatted[^cfg.MaxConversationCharsForSummary..];
            summaryInputCharsAfter = formatted.Length;
        }

        var mustPreserve = request.Plan?.MustPreserveAppendix;
        var promptBody = BuildSummaryPrompt(cfg.SummaryPrompt, formatted, mustPreserve);
        string summary;
        try
        {
            var summaryResponse = await modelClient.CompleteAsync(
                new AgentModelRequest(
                    new[] { new AgentModelMessage("user", promptBody) },
                    Array.Empty<ToolDefinition>(),
                    AllowToolCalls: false,
                    MaxTokens: cfg.SummaryMaxTokens),
                cancellationToken: cancellationToken);

            summary = summaryResponse.Content.Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = "(Summary unavailable)";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Summarization LLM call failed for session {SessionId}", session.Id);
            summary = "(Summarization failed: " + ex.Message + ")";
        }

        var summaryMessage = SummaryMessageBuilder.CreateSummaryPlaceholder(summary, transcriptPath);
        var compactMessages = new List<ChatMessage>();

        var strategy = request.Force
            ? CompactionStrategy.ForceCompact
            : CompactionStrategy.ConversationCompact;
        var layers = new List<CompactionLayer> { CompactionLayer.ConversationCompact };
        if (truncateArgsApplied)
        {
            layers.Insert(0, CompactionLayer.TruncateArgs);
        }

        if (request.Plan?.ApplyPrefixReEvict == true)
        {
            layers.Insert(0, CompactionLayer.ToolResultEviction);
        }

        var pressure = request.Plan?.Pressure;
        var utilization = request.RuntimeContext?.Budget.TotalUtilization;
        var tokensAfterPreview = ContextTokenEstimator.Estimate(
            new[] { summaryMessage }.Concat(tail).ToArray(),
            cfg.IncludeReasoningInModelContext);

        if (request.EmitAudit)
        {
            var auditContent = CompactionMessageContent.CreateConversationCompact(
                tokensBefore,
                tokensAfterPreview,
                originalCount,
                transcriptPath,
                summary,
                strategy,
                layers,
                pressure,
                utilization,
                summaryInputCharsBefore,
                summaryInputCharsAfter,
                hygieneSavingsEstimate);
            compactMessages.Add(CompactionMessageContent.CreateCompactionMessage(auditContent));
        }

        compactMessages.Add(summaryMessage);
        compactMessages.AddRange(tail);

        await storage.SaveContextSummaryAsync(
            new ContextSummary(
                Guid.NewGuid().ToString("N"),
                session.Id,
                summary,
                originalCount,
                DateTimeOffset.UtcNow),
            cancellationToken);

        session = session.WithMessages(compactMessages);
        sessionUsageAccumulator.RecordCompaction(session.Id, tokensBefore, tokensAfterPreview);

        _logger.Information(
            "Compacted session {SessionId} from {OriginalCount} to {ResultCount} messages (kind {Kind}, force {Force})",
            session.Id,
            originalCount,
            session.Messages.Count,
            request.Kind,
            request.Force);

        return new ConversationCompactResult(session, true);
    }

    private static string BuildSummaryPrompt(string template, string formattedMessages, string? mustPreserveAppendix)
    {
        var mustPreserve = string.IsNullOrWhiteSpace(mustPreserveAppendix) ? string.Empty : mustPreserveAppendix.Trim();
        return template
            .Replace("{must_preserve}", mustPreserve, StringComparison.Ordinal)
            .Replace("{messages}", formattedMessages, StringComparison.Ordinal);
    }
}
