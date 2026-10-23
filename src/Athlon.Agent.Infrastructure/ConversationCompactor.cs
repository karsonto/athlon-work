using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Athlon.Agent.Core.RuntimeDiagnostics;
using System.Diagnostics;

namespace Athlon.Agent.Infrastructure;

public sealed partial class ConversationCompactor(
    AppSettings settings,
    IAgentModelClient modelClient,
    IFileStorageService storage,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IAppLogger logger,
    IAgentRunContextAccessor? runContextAccessor = null,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null) : IConversationCompactor
{
    private readonly IAppLogger _logger = logger.ForContext("ConversationCompactor");
    private readonly IAgentRunContextAccessor? _runContextAccessor = runContextAccessor;
    private readonly IRuntimeDiagnosticEventSink? _runtimeDiagnosticEventSink = runtimeDiagnosticEventSink;

    public async Task<ConversationCompactResult> CompactIfNeededAsync(
        AgentSession session,
        CompactionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var cfg = settings.ContextCompaction;
        var conversation = ConversationMessageFilters.WithoutHandoffNotes(
            ConversationMessageFilters.WithoutCompactionAudits(session.Messages));
        if (conversation.Count == 0)
        {
            return new ConversationCompactResult(session, false);
        }

        var isManualCompact = request.Strategy == CompactionStrategy.ManualCompact;
        var estimatedTokens = ContextTokenEstimator.ResolveEffectiveEstimate(
            conversation,
            cfg,
            request.RuntimeContext?.Budget,
            request.RuntimeContext?.RawHistoryEstimate);
        var pressure = request.Plan?.Pressure ?? ContextPressureLevel.Normal;
        var shouldCompact = isManualCompact
            || (request.RuntimeContext is { } runtime && cfg.DynamicCompaction.Enabled
                ? ContextPressureEvaluator.ShouldCompact(
                    runtime.Budget,
                    conversation,
                    cfg,
                    pressure,
                    request.Force)
                : ConversationCutoffPlanner.ShouldCompact(conversation, estimatedTokens, cfg, request.Force));

        if (!shouldCompact)
        {
            return new ConversationCompactResult(session, false);
        }

        if (request.RuntimeContext?.Budget is { } budgetSnap
            && ContextPressureEvaluator.IsFixedOverheadDominating(budgetSnap, cfg))
        {
            CompactionDecisionLog.Write(
                _logger,
                _runtimeDiagnosticEventSink,
                session.Id,
                outcome: "skipped",
                reason: "fixed_overhead_dominates",
                pressure: pressure,
                tokensBefore: estimatedTokens,
                keepBudget: request.Plan?.RetainUserTokenBudget);
            return new ConversationCompactResult(session, false);
        }

        var retainBudget = request.Plan?.RetainUserTokenBudget
            ?? CheckpointHistoryBuilder.ResolveRetainUserTokenBudget(cfg, pressure, request.Force);
        var split = CheckpointHistoryBuilder.TrySplit(conversation, retainBudget);
        if (split is null || split.PrefixToSummarize.Count == 0)
        {
            CompactionDecisionLog.Write(
                _logger,
                _runtimeDiagnosticEventSink,
                session.Id,
                outcome: "skipped",
                reason: "cutoff_zero",
                pressure: pressure,
                tokensBefore: estimatedTokens,
                keepBudget: retainBudget);
            return new ConversationCompactResult(session, false);
        }

        var prefix = split.PrefixToSummarize;
        var retainedTail = split.RetainedUsers.Concat(split.PairingTail).ToList();
        var originalCount = conversation.Count;
        var tokensBefore = estimatedTokens;

        string? transcriptPath = null;
        if (cfg.OffloadBeforeCompact)
        {
            transcriptPath = await storage.SaveTranscriptAsync(session.Id, session.Messages, cancellationToken);
        }

        var handoffMessage = await LoadHandoffMessageAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var summaryRequest = BuildSummaryRequest(
            prefix,
            cfg,
            request,
            mustPreserve: null,
            request.EmitAudit,
            handoffMessage is not null,
            out var summaryInputCharsBefore,
            out var summaryInputCharsAfter,
            out var hygieneSavingsEstimate);

        var (summary, usedTemplate) = await SummarizeWithRetryAsync(
            session,
            prefix,
            cfg,
            pressure,
            request.Force,
            summaryRequest,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            CompactionDecisionLog.Write(
                _logger,
                _runtimeDiagnosticEventSink,
                session.Id,
                outcome: "skipped",
                reason: "summary_failed",
                pressure: pressure,
                cutoff: split.SummarizedEndExclusive,
                tokensBefore: tokensBefore,
                keepBudget: retainBudget);
            return new ConversationCompactResult(session, false);
        }

        var summaryMessage = SummaryMessageBuilder.CreateSummaryPlaceholder(
            summary,
            transcriptPath,
            handoffAttached: handoffMessage is not null);
        var compactMessages = new List<ChatMessage>();
        var utilization = request.RuntimeContext?.Budget.TotalUtilization;

        if (request.EmitAudit)
        {
            var auditContent = CompactionMessageContent.CreateConversationCompact(
                tokensBefore,
                EstimateCompactedTokens(summaryMessage, retainedTail, cfg, handoffMessage),
                originalCount,
                transcriptPath,
                summary,
                request.Strategy,
                [CompactionLayer.ConversationCompact],
                pressure,
                utilization,
                summaryInputCharsBefore,
                summaryInputCharsAfter,
                hygieneSavingsEstimate);
            compactMessages.Add(CompactionMessageContent.CreateCompactionMessage(auditContent));
        }

        compactMessages.Add(summaryMessage);
        if (handoffMessage is not null)
        {
            compactMessages.Add(handoffMessage);
        }

        compactMessages.AddRange(retainedTail);
        var tokensAfterPreview = EstimateCompactedTokens(summaryMessage, retainedTail, cfg, handoffMessage);

        await storage.SaveContextSummaryAsync(
            new ContextSummary(
                IdGen.NewId(),
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

        var summaryCount = session.Messages.Count(SummaryMessageBuilder.IsSummaryMessage);
        if (summaryCount >= Math.Max(1, cfg.CompactionQualityWarnAfterCount))
        {
            _logger.Warning(
                "Session {SessionId} has {SummaryCount} compaction checkpoints; long multi-compact threads may lose accuracy",
                session.Id,
                summaryCount);
        }

        _logger.Information(
            "Checkpointed session {SessionId} from {OriginalCount} to {ResultCount} messages (kind {Kind}, force {Force})",
            session.Id,
            originalCount,
            session.Messages.Count,
            request.Kind,
            request.Force);

        CompactionDecisionLog.Write(
            _logger,
            _runtimeDiagnosticEventSink,
            session.Id,
            outcome: usedTemplate ? "template" : "compacted",
            reason: usedTemplate ? "summary_failed" : "ok",
            pressure: pressure,
            cutoff: split.SummarizedEndExclusive,
            tailMessages: retainedTail.Count,
            tokensBefore: tokensBefore,
            projectedAfter: tokensAfterPreview,
            keepBudget: retainBudget);

        return new ConversationCompactResult(session, true);
    }

    private async Task<(string Summary, bool UsedTemplate)> SummarizeWithRetryAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> prefix,
        ContextCompactionSettings cfg,
        ContextPressureLevel pressure,
        bool force,
        AgentModelRequest summaryRequest,
        CancellationToken cancellationToken)
    {
        var allowTemplate = cfg.EnableTemplateSummaryOnOverflowFailure
            && (force || pressure == ContextPressureLevel.Overflow);
        var maxAttempts = allowTemplate ? 2 : 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var summaryAttemptId = IdGen.NewId();
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

                var text = summaryResponse.Content.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return (text, false);
                }

                _logger.Warning(
                    "Summarization returned empty content for session {SessionId} (attempt {Attempt})",
                    session.Id,
                    attempt);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                summaryStopwatch.Stop();
                var context = _runContextAccessor.Current;
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
                _logger.Error(ex, "Summarization LLM call failed for session {SessionId} (attempt {Attempt})", session.Id, attempt);

                var evt = new RuntimeDiagnosticEvent(
                    eventId: "",
                    ts: default,
                    sequence: 0,
                    sessionId: session.Id,
                    runId: context?.RunId,
                    turnId: null,
                    attemptId: summaryAttemptId,
                    parentAttemptId: null,
                    toolCallId: null,
                    messageId: null,
                    component: RuntimeDiagnosticComponent.Compaction,
                    phase: RuntimeDiagnosticPhase.Persist,
                    eventType: "compaction.summary_failed",
                    severity: RuntimeDiagnosticSeverity.Error,
                    errorCode: RuntimeDiagnosticErrorCodes.CompactionSummaryFailed,
                    message: ex.Message);
                if (_runtimeDiagnosticEventSink is { } sink)
                {
                    await sink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        if (allowTemplate)
        {
            var template = TemplateSummaryBuilder.Build(prefix, cfg, mustPreserveAppendix: null);
            if (!string.IsNullOrWhiteSpace(template))
            {
                return (template, true);
            }
        }

        return (string.Empty, false);
    }
}
