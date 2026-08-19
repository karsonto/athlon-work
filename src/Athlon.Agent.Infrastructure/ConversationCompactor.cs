using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure.BehaviorReport;
using System.Diagnostics;

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

        var isManualCompact = request.Strategy == CompactionStrategy.ManualCompact;
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
            request.RuntimeContext?.Budget,
            request.RuntimeContext?.RawHistoryEstimate);
        var shouldCompact = isManualCompact
            || (request.RuntimeContext is { } runtime && cfg.DynamicCompaction.Enabled
                ? ContextPressureEvaluator.ShouldCompact(
                    runtime.Budget,
                    conversation,
                    cfg,
                    request.Plan?.Pressure ?? ContextPressureLevel.Normal,
                    request.Force)
                : ConversationCutoffPlanner.ShouldCompact(conversation, estimatedTokens, cfg, request.Force));

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
        if (cutoff <= 0 && isManualCompact)
        {
            cutoff = ResolveManualCompactCutoff(conversation, cfg);
        }
        else if (cutoff <= 0
            && request.Force
            && request.Strategy == CompactionStrategy.ForceCompact
            && conversation.Count > 1)
        {
            var keepCount = cfg.KeepMessages > 0
                ? Math.Min(cfg.KeepMessages, conversation.Count - 1)
                : 1;
            keepCount = Math.Max(1, Math.Min(keepCount, conversation.Count - 1));
            cutoff = ConversationCutoffPlanner.FindSafeCutoffPoint(
                conversation,
                conversation.Count - keepCount);
        }

        if (cutoff <= 0)
        {
            _logger.Debug("Compaction triggered but safe cutoff is 0 — skipping");
            return new ConversationCompactResult(session, false);
        }

        // Keep prior __compaction_summary__ placeholders in the prefix so repeated
        // compaction can fold condensed context instead of dropping it.
        var prefix = conversation.Take(cutoff).ToList();
        var tail = conversation.Skip(cutoff).ToList();
        var originalCount = conversation.Count;
        var tokensBefore = estimatedTokens;

        string? transcriptPath = null;
        if (cfg.OffloadBeforeCompact)
        {
            transcriptPath = await storage.SaveTranscriptAsync(session.Id, session.Messages, cancellationToken);
        }

        var mustPreserve = request.Plan?.MustPreserveAppendix;
        var summaryRequest = BuildSummaryRequest(
            prefix,
            cfg,
            request,
            mustPreserve,
            out var summaryInputCharsBefore,
            out var summaryInputCharsAfter,
            out var hygieneSavingsEstimate);
        string summary;
        var summaryAttemptId = Guid.NewGuid().ToString("N");
        var summaryStopwatch = Stopwatch.StartNew();
        try
        {
            var summaryResponse = await modelClient.CompleteAsync(
                summaryRequest,
                cancellationToken: cancellationToken);
            var usage = ModelUsageAccounting.Resolve(summaryRequest, summaryResponse);
            summaryStopwatch.Stop();
            sessionUsageAccumulator.RecordCall(
                session.Id, summaryAttemptId, ModelCallPurpose.Summary, usage);
            await storage.AppendAttemptEventAsync(
                session.Id,
                new AgentAttemptEvent(
                    DateTimeOffset.UtcNow, summaryAttemptId, session.Id, session.Id,
                    AgentAttemptKind.Model, ModelCallPurpose.Summary, null,
                    ToolCatalogFingerprint.Compute(summaryRequest.Tools), session.ModelName,
                    usage.PromptTokens ?? 0, usage.CompletionTokens ?? 0, "success", null,
                    summaryStopwatch.ElapsedMilliseconds),
                cancellationToken).ConfigureAwait(false);

            summary = summaryResponse.Content.Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                _logger.Warning(
                    "Summarization returned empty content for session {SessionId}; aborting compaction",
                    session.Id);
                return new ConversationCompactResult(session, false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            summaryStopwatch.Stop();
            var promptTokens = ContextTokenEstimator.EstimateModelRequest(summaryRequest);
            sessionUsageAccumulator.RecordCall(
                session.Id,
                summaryAttemptId,
                ModelCallPurpose.Summary,
                new ModelUsage(promptTokens, 0, promptTokens));
            await storage.AppendAttemptEventAsync(
                session.Id,
                new AgentAttemptEvent(
                    DateTimeOffset.UtcNow, summaryAttemptId, session.Id, session.Id,
                    AgentAttemptKind.Model, ModelCallPurpose.Summary, null,
                    ToolCatalogFingerprint.Compute(summaryRequest.Tools), session.ModelName,
                    promptTokens, 0, "failure",
                    ex.GetType().Name, summaryStopwatch.ElapsedMilliseconds),
                CancellationToken.None).ConfigureAwait(false);
            _logger.Error(ex, "Summarization LLM call failed for session {SessionId}; aborting compaction", session.Id);
            return new ConversationCompactResult(session, false);
        }

        var summaryMessage = SummaryMessageBuilder.CreateSummaryPlaceholder(summary, transcriptPath);
        var compactMessages = new List<ChatMessage>();

        var strategy = request.Strategy;
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
        try
        {
            BehaviorEventManager.Instance.Record(
                BehaviorEventIds.Context,
                BehaviorEventTypes.Event,
                BehaviorEventIds.Context,
                new Dictionary<string, object?>
                {
                    ["action"] = "compaction",
                    ["session_id"] = session.Id,
                    ["tokens_before"] = tokensBefore,
                    ["tokens_after"] = tokensAfterPreview,
                    ["savings"] = Math.Max(0, tokensBefore - tokensAfterPreview)
                });
        }
        catch
        {
            // ignore
        }

        _logger.Information(
            "Compacted session {SessionId} from {OriginalCount} to {ResultCount} messages (kind {Kind}, force {Force})",
            session.Id,
            originalCount,
            session.Messages.Count,
            request.Kind,
            request.Force);

        return new ConversationCompactResult(session, true);
    }

    private static int ResolveManualCompactCutoff(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings cfg)
    {
        if (conversation.Count == 0)
        {
            return 0;
        }

        if (conversation.Count == 1)
        {
            return 1;
        }

        var keepCount = cfg.KeepMessages > 0
            ? Math.Min(cfg.KeepMessages, conversation.Count - 1)
            : 1;
        keepCount = Math.Max(1, Math.Min(keepCount, conversation.Count - 1));
        return ConversationCutoffPlanner.FindSafeCutoffPoint(conversation, conversation.Count - keepCount);
    }

    private static AgentModelRequest BuildSummaryRequest(
        IReadOnlyList<ChatMessage> prefix,
        ContextCompactionSettings cfg,
        CompactionExecutionRequest request,
        string? mustPreserve,
        out int? summaryInputCharsBefore,
        out int? summaryInputCharsAfter,
        out int? hygieneSavingsEstimate)
    {
        var pressure = request.Plan?.Pressure;
        var effectiveMaxChars = ResolveSummaryMaxChars(cfg, pressure, request.Force);
        var effectiveMaxTokens = ResolveSummaryMaxTokens(cfg, pressure, request.Force);
        var hygieneSettings = ResolveSummaryHygieneSettings(cfg, pressure, request.Force);
        var runtime = request.RuntimeContext;
        var environmentPrompt = runtime?.EnvironmentPrompt ?? string.Empty;
        var calibrationMultiplier = runtime?.CalibrationMultiplier ?? 1.0;

        var formatted = ConversationSummaryFormatter.FormatMessages(prefix);
        summaryInputCharsBefore = formatted.Length;
        summaryInputCharsAfter = formatted.Length;
        hygieneSavingsEstimate = null;

        if (formatted.Length > effectiveMaxChars
            || ContextTokenEstimator.EstimateTextTokens(formatted, calibrationMultiplier) > hygieneSettings.MaxToolResultTokens)
        {
            var compacted = RequestHistoryHygiene.CompactTextForSummary(formatted, hygieneSettings);
            formatted = compacted.Text;
            summaryInputCharsBefore = compacted.CharsBefore;
            summaryInputCharsAfter = compacted.CharsAfter;
            hygieneSavingsEstimate = compacted.EstimatedSavingsTokens;
        }

        if (formatted.Length > effectiveMaxChars)
        {
            formatted = ConversationSummaryFormatter.FitToMaxChars(formatted, effectiveMaxChars);
            summaryInputCharsAfter = formatted.Length;
        }

        var built = ModelMessagesForApiBuilder.Build(
            cache: null,
            environmentPrompt,
            prefix,
            cfg);
        var hygieneResult = RequestHistoryHygiene.ApplyToModelMessages(built.Messages, hygieneSettings);
        var messages = hygieneResult.Messages.ToList();
        hygieneSavingsEstimate = Math.Max(
            hygieneSavingsEstimate ?? 0,
            built.EstimatedSavingsTokens + hygieneResult.EstimatedSavingsTokens);

        // Keep the summary instruction stable so providers can reuse as much prompt prefix as possible.
        messages.Add(new AgentModelMessage(
            "user",
            BuildSummaryPrompt(
                cfg.SummaryPrompt,
                ConversationCompactionDefaults.PrecedingMessagesPlaceholder,
                mustPreserve)));

        return new AgentModelRequest(
            messages,
            runtime?.Tools ?? Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: effectiveMaxTokens);
    }

    private static int ResolveSummaryMaxTokens(
        ContextCompactionSettings cfg,
        ContextPressureLevel? pressure,
        bool force) =>
        force || pressure == ContextPressureLevel.Overflow
            ? Math.Max(128, Math.Min(cfg.SummaryMaxTokens, cfg.SummaryMaxTokens / 2))
            : cfg.SummaryMaxTokens;

    private static int ResolveSummaryMaxChars(
        ContextCompactionSettings cfg,
        ContextPressureLevel? pressure,
        bool force) =>
        force || pressure == ContextPressureLevel.Overflow
            ? Math.Max(1024, Math.Min(cfg.MaxConversationCharsForSummary, cfg.MaxConversationCharsForSummary / 4))
            : cfg.MaxConversationCharsForSummary;

    private static RequestHistoryHygieneSettings ResolveSummaryHygieneSettings(
        ContextCompactionSettings cfg,
        ContextPressureLevel? pressure,
        bool force)
    {
        if (!force && pressure != ContextPressureLevel.Overflow)
        {
            return cfg.RequestHistoryHygiene;
        }

        return new RequestHistoryHygieneSettings
        {
            Enabled = cfg.RequestHistoryHygiene.Enabled,
            MaxToolResultLines = Math.Max(16, Math.Min(cfg.RequestHistoryHygiene.MaxToolResultLines, cfg.RequestHistoryHygiene.MaxToolResultLines / 2)),
            MaxToolResultBytes = Math.Max(1024, Math.Min(cfg.RequestHistoryHygiene.MaxToolResultBytes, cfg.RequestHistoryHygiene.MaxToolResultBytes / 2)),
            MaxToolResultTokens = Math.Max(256, Math.Min(cfg.RequestHistoryHygiene.MaxToolResultTokens, cfg.RequestHistoryHygiene.MaxToolResultTokens / 2)),
            MaxToolArgumentStringBytes = Math.Max(256, Math.Min(cfg.RequestHistoryHygiene.MaxToolArgumentStringBytes, cfg.RequestHistoryHygiene.MaxToolArgumentStringBytes / 2)),
            MaxToolArgumentStringTokens = Math.Max(64, Math.Min(cfg.RequestHistoryHygiene.MaxToolArgumentStringTokens, cfg.RequestHistoryHygiene.MaxToolArgumentStringTokens / 2)),
            MaxArrayItems = cfg.RequestHistoryHygiene.MaxArrayItems
        };
    }

    private static string BuildSummaryPrompt(string template, string formattedMessages, string? mustPreserveAppendix)
    {
        var mustPreserve = string.IsNullOrWhiteSpace(mustPreserveAppendix) ? string.Empty : mustPreserveAppendix.Trim();
        return template
            .Replace("{must_preserve}", mustPreserve, StringComparison.Ordinal)
            .Replace("{messages}", formattedMessages, StringComparison.Ordinal);
    }
}
