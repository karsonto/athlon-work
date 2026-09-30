using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Athlon.Agent.Core.RuntimeDiagnostics;
using System.Diagnostics;

namespace Athlon.Agent.Infrastructure;

public sealed partial class ConversationCompactor(
    AppSettings settings,
    IAgentModelClient modelClient,
    IFileStorageService storage,
    TruncateArgsService truncateArgsService,
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
        if (request.Strategy == CompactionStrategy.MiddleCutOnRetrySkipped)
        {
            return await ApplyMiddleCutCompactAsync(session, request, cancellationToken).ConfigureAwait(false);
        }

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

        // The semantic planner is the only path that can advance the cutoff past the user message
        // that opened the current turn, so it is used whenever the caller supplied a keep budget or
        // semantic cutoff is on. Without it, only the legacy message/token windows apply.
        var keepTokenBudget = request.Plan?.KeepTokenBudget;
        var cutPlan = ResolveCutPlan(conversation, cfg, estimatedTokens, keepTokenBudget);
        if (cutPlan.SummarizedEnd <= 0 && isManualCompact)
        {
            cutPlan = SemanticCutoffPlanner.CreatePlanForCutoff(
                conversation,
                ResolveManualCompactCutoff(conversation, cfg));
        }
        else if (cutPlan.SummarizedEnd <= 0
            && request.Force
            && request.Strategy is CompactionStrategy.ForceCompact or CompactionStrategy.ConversationCompact
            && conversation.Count > 1)
        {
            var keepCount = cfg.KeepMessages > 0
                ? Math.Min(cfg.KeepMessages, conversation.Count - 1)
                : 1;
            keepCount = Math.Max(1, Math.Min(keepCount, conversation.Count - 1));
            cutPlan = SemanticCutoffPlanner.CreatePlanForCutoff(
                conversation,
                conversation.Count - keepCount);
        }

        var cutoff = cutPlan.SummarizedEnd;
        if (cutoff <= 0)
        {
            _logger.Debug("Compaction triggered but safe cutoff is 0 — skipping");
            return new ConversationCompactResult(session, false);
        }

        // Keep prior __compaction_summary__ placeholders in the prefix so repeated
        // compaction can fold condensed context instead of dropping it.
        var prefix = conversation.Take(cutoff).ToList();
        var tail = conversation.Skip(cutoff).ToList();
        var reattachedPlan = cutPlan.NeedsPlanReattach
            ? conversation[cutPlan.PlanAnchorIndex!.Value]
            : null;
        var reattachedUser = cutPlan.NeedsUserReattach
            ? conversation[cutPlan.UserAnchorIndex!.Value]
            : null;
        var originalCount = conversation.Count;
        var tokensBefore = estimatedTokens;

        // Idle guard: compaction must not spend a summary round trip when the projected payload
        // barely shrinks. This is checked on the projected post-state (summary upper bound + tail)
        // so a no-op pass costs nothing. Manual compaction is explicit user intent, so it is exempt,
        // and non-dynamic mode keeps its historical behaviour.
        if (cfg.DynamicCompaction.Enabled
            && !isManualCompact
            && !request.Force
            && cfg.MinCompactionSavingsTokens > 0)
        {
            var projectedAfter = EstimateProjectedAfterTokens(
                tail,
                reattachedPlan,
                reattachedUser,
                cfg,
                ResolveSummaryMaxTokens(cfg, request.Plan?.Pressure, request.Force));
            if (tokensBefore - projectedAfter < cfg.MinCompactionSavingsTokens)
            {
                _logger.Debug(
                    "Skipped conversation compact for session {SessionId}: projected savings {Savings} < {Minimum}",
                    session.Id,
                    tokensBefore - projectedAfter,
                    cfg.MinCompactionSavingsTokens);
                return new ConversationCompactResult(session, false);
            }
        }

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
            request.EmitAudit,
            out var summaryInputCharsBefore,
            out var summaryInputCharsAfter,
            out var hygieneSavingsEstimate);
        string summary;
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

            summary = SummaryPreservation.EnsureFacts(summaryResponse.Content.Trim(), mustPreserve);
            if (string.IsNullOrWhiteSpace(summary))
            {
                _logger.Warning(
                    "Summarization returned empty content for session {SessionId}; aborting compaction",
                    session.Id);
                var context = _runContextAccessor.Current;
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
                    message: "Empty summary");
                if (_runtimeDiagnosticEventSink is { } sink)
                {
                    await sink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
                }
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
            _logger.Error(ex, "Summarization LLM call failed for session {SessionId}; aborting compaction", session.Id);

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
            return new ConversationCompactResult(session, false);
        }

        var summaryMessage = SummaryMessageBuilder.CreateSummaryPlaceholder(summary, transcriptPath);
        var handoffMessage = await LoadHandoffMessageAsync(session.Id, cancellationToken).ConfigureAwait(false);
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

        if (request.EmitAudit)
        {
            var auditContent = CompactionMessageContent.CreateConversationCompact(
                tokensBefore,
                EstimateCompactedTokens(summaryMessage, reattachedPlan, reattachedUser, tail, cfg, handoffMessage),
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
        if (handoffMessage is not null)
        {
            compactMessages.Add(handoffMessage);
        }

        // Approved plans are control messages, so they are not the protected-tail anchor. When the
        // cut still covers one, keep the body verbatim ahead of the active user instruction.
        if (reattachedPlan is not null)
        {
            compactMessages.Add(reattachedPlan);
        }

        // The cutoff can advance past the user message that opened the current turn. Re-attach it
        // verbatim after the summary so the active instruction survives the cut instead of relying
        // on the summary alone.
        if (reattachedUser is not null)
        {
            compactMessages.Add(reattachedUser);
        }

        compactMessages.AddRange(tail);
        var tokensAfterPreview = EstimateCompactedTokens(summaryMessage, reattachedPlan, reattachedUser, tail, cfg, handoffMessage);

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

        _logger.Information(
            "Compacted session {SessionId} from {OriginalCount} to {ResultCount} messages (kind {Kind}, force {Force})",
            session.Id,
            originalCount,
            session.Messages.Count,
            request.Kind,
            request.Force);

        return new ConversationCompactResult(session, true);
    }


}
