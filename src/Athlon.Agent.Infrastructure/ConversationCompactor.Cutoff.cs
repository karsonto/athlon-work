using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Athlon.Agent.Core.RuntimeDiagnostics;
using System.Diagnostics;

namespace Athlon.Agent.Infrastructure;

/// <summary>Middle-cut planning and token budgets.</summary>
public sealed partial class ConversationCompactor
{
    private async Task<ConversationCompactResult> ApplyMiddleCutCompactAsync(
        AgentSession session,
        CompactionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var cfg = settings.ContextCompaction;
        var conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
        if (conversation.Count == 0)
        {
            return new ConversationCompactResult(session, false);
        }

        var keepHead = Math.Max(1, cfg.MiddleCutKeepHeadMessages);
        var keepTail = Math.Max(1, cfg.MiddleCutKeepTailMessages);
        if (conversation.Count <= keepHead + keepTail + 1)
        {
            return new ConversationCompactResult(session, false);
        }

        // Grow the retained head forward to a pairing-balanced prefix. A head ending on an assistant
        // turn whose tool_call is answered inside the dropped middle would ship an unanswered
        // tool_call, which the API rejects. Unlike the tail, the head count is a floor: extending it
        // by a message or two keeps the tool pair whole, while trimming back could empty the window.
        keepHead = ConversationCutoffPlanner.FindNextBalancedPrefixEnd(conversation, keepHead);
        if (keepHead >= conversation.Count)
        {
            return new ConversationCompactResult(session, false);
        }

        // Drop the middle span between a retained head and a retained tail. Moving the tail start
        // forward keeps the drop boundary from splitting a tool pair: starting the tail on a
        // tool_result would leave its tool_call behind in the summarized middle.
        var tailStart = conversation.Count - keepTail;
        while (tailStart < conversation.Count && conversation[tailStart].Role == MessageRole.Tool)
        {
            tailStart++;
        }

        if (tailStart >= conversation.Count || tailStart <= keepHead)
        {
            return new ConversationCompactResult(session, false);
        }

        var middleStart = keepHead;
        var middleCount = tailStart - keepHead;
        if (middleCount <= 0)
        {
            return new ConversationCompactResult(session, false);
        }

        var head = conversation.Take(keepHead).ToList();
        var middle = conversation.Skip(middleStart).Take(middleCount).ToList();
        var tail = conversation.Skip(tailStart).ToList();
        ChatMessage? reattachedPlan = null;
        var summarizedMiddle = new List<ChatMessage>(middle.Count);
        foreach (var message in middle)
        {
            if (ApprovedPlanPrompt.IsApprovedPlanMessage(message))
            {
                reattachedPlan = message;
                continue;
            }

            summarizedMiddle.Add(message);
        }

        if (summarizedMiddle.Count == 0)
        {
            return new ConversationCompactResult(session, false);
        }

        var summaryRequest = BuildSummaryRequest(
            summarizedMiddle,
            cfg,
            request,
            request.Plan?.MustPreserveAppendix,
            computeAuditMetrics: false,
            out _,
            out _,
            out _);

        string summary;
        var summaryAttemptId = IdGen.NewId();
        var summaryStopwatch = Stopwatch.StartNew();
        try
        {
            var summaryResponse = await modelClient.CompleteAsync(summaryRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            var usage = ModelUsageAccounting.Resolve(summaryRequest, summaryResponse);
            summaryStopwatch.Stop();
            sessionUsageAccumulator.RecordCall(
                session.Id, summaryAttemptId, ModelCallPurpose.Summary, usage);
            summary = SummaryPreservation.EnsureFacts(summaryResponse.Content.Trim(), request.Plan?.MustPreserveAppendix);
            if (string.IsNullOrWhiteSpace(summary))
            {
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
            var context = _runContextAccessor?.Current;
            if (_runtimeDiagnosticEventSink is { } sink)
            {
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
                await sink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
            }

            return new ConversationCompactResult(session, false);
        }

        var hiddenSummary = SummaryMessageBuilder.CreateSummaryPlaceholder(summary, transcriptPath: null, hiddenFromTimeline: true);
        var handoffMessage = await LoadHandoffMessageAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var compactMessages = new List<ChatMessage>(head.Count + tail.Count + 3);
        compactMessages.AddRange(head);
        compactMessages.Add(hiddenSummary);
        if (handoffMessage is not null)
        {
            compactMessages.Add(handoffMessage);
        }
        if (reattachedPlan is not null)
        {
            compactMessages.Add(reattachedPlan);
        }

        compactMessages.AddRange(tail);

        if (request.EmitAudit)
        {
            var auditContent = CompactionMessageContent.CreateConversationCompact(
                tokensBefore: ContextTokenEstimator.Estimate(
                    conversation,
                    cfg.IncludeReasoningInModelContext,
                    hygiene: cfg.RequestHistoryHygiene),
                tokensAfter: ContextTokenEstimator.Estimate(
                    compactMessages,
                    cfg.IncludeReasoningInModelContext,
                    hygiene: cfg.RequestHistoryHygiene),
                originalMessageCount: conversation.Count,
                transcriptPath: null,
                summaryPreview: "Middle-cut compaction applied due to overflow retry skip.",
                strategy: CompactionStrategy.MiddleCutOnRetrySkipped,
                layers: [CompactionLayer.ConversationCompact],
                pressureLevel: request.Plan?.Pressure,
                utilization: request.RuntimeContext?.Budget.TotalUtilization);
            compactMessages.Insert(0, CompactionMessageContent.CreateCompactionMessage(auditContent));
        }

        session = session.WithMessages(compactMessages);

        var runContext = _runContextAccessor?.Current;
        if (_runtimeDiagnosticEventSink is { } runtimeSink)
        {
            var evt = new RuntimeDiagnosticEvent(
                eventId: "",
                ts: default,
                sequence: 0,
                sessionId: session.Id,
                runId: runContext?.RunId ?? session.Id,
                turnId: null,
                attemptId: summaryAttemptId,
                parentAttemptId: null,
                toolCallId: null,
                messageId: null,
                component: RuntimeDiagnosticComponent.Compaction,
                phase: RuntimeDiagnosticPhase.Compact,
                eventType: "compaction.middle_cut_applied",
                severity: RuntimeDiagnosticSeverity.Warning,
                errorCode: RuntimeDiagnosticErrorCodes.CompactionMiddleCutApplied,
                message: $"keptHead={keepHead}, keptTail={keepTail}, droppedMiddle={middleCount}, summaryChars={summary.Length}");
            await runtimeSink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }

        return new ConversationCompactResult(session, true);
    }

    /// <summary>
    /// Picks the cut plan for this request.
    ///
    /// <para>Under dynamic compaction the semantic planner owns the cut: it is the only path that
    /// can advance the cutoff past the user message that opened the current turn, and it applies
    /// <see cref="ContextCompactionSettings.ProtectedTailMaxMessages"/>. Everywhere else the legacy
    /// message/token window is preserved verbatim so non-dynamic behaviour is unchanged.</para>
    /// </summary>
    private static ConversationCutPlan ResolveCutPlan(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings cfg,
        int estimatedTokens,
        int? keepTokenBudget)
    {
        var semanticCutoffEnabled = cfg.DynamicCompaction.Enabled
            && cfg.DynamicCompaction.EnableSemanticCutoff;
        if (semanticCutoffEnabled)
        {
            var plan = SemanticCutoffPlanner.DetermineCutPlan(
                conversation,
                cfg,
                keepTokenBudget is > 0
                    ? keepTokenBudget.Value
                    : ResolveSemanticKeepBudget(conversation, cfg, estimatedTokens));
            if (plan.SummarizedEnd > 0)
            {
                return plan;
            }
        }

        var legacyCutoff = ConversationCutoffPlanner.DetermineCutoffIndex(
            conversation,
            estimatedTokens,
            cfg,
            keepTokenBudget);
        return legacyCutoff <= 0
            ? new ConversationCutPlan(0, conversation.Count, null)
            : SemanticCutoffPlanner.CreatePlanForCutoff(conversation, legacyCutoff);
    }

    /// <summary>
    /// Keep budget to hand the semantic planner when the caller did not compute one (manual
    /// compaction, forced passes). Mirrors the static keep floor so the retained tail stays in the
    /// same size band as the non-semantic planner.
    /// </summary>
    private static int ResolveSemanticKeepBudget(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings cfg,
        int estimatedTokens)
    {
        if (cfg.KeepTokens > 0)
        {
            return cfg.KeepTokens;
        }

        if (cfg.KeepMessages > 0 && conversation.Count > cfg.KeepMessages)
        {
            var tailStart = conversation.Count - cfg.KeepMessages;
            return ContextTokenEstimator.EstimateSuffix(
                conversation,
                tailStart,
                cfg.IncludeReasoningInModelContext,
                cfg.MaxToolScreenshotsInModelContext,
                cfg.RequestHistoryHygiene);
        }

        // No keep hint: allow the planner to target a modest share of the current history so the
        // protected-tail cap decides the cut rather than a zero budget short-circuiting it.
        return Math.Max(1, estimatedTokens / 4);
    }

    /// <summary>
    /// Upper-bound estimate of the post-compaction payload: the summary is charged at
    /// <paramref name="summaryMaxTokens"/> (its hard cap) so the guard never under-estimates.
    /// </summary>
    private static int EstimateProjectedAfterTokens(
        IReadOnlyList<ChatMessage> tail,
        ChatMessage? reattachedPlan,
        ChatMessage? reattachedUser,
        ContextCompactionSettings cfg,
        int summaryMaxTokens)
    {
        // Envelope text: summary markers plus the optional transcript-path preamble.
        const int SummaryEnvelopeSlackTokens = 128;
        var summaryTokens = summaryMaxTokens
            + SummaryEnvelopeSlackTokens
            + ContextTokenEstimator.EstimateMessage(
                SummaryMessageBuilder.CreateSummaryPlaceholder(string.Empty, null),
                cfg.IncludeReasoningInModelContext);

        var total = summaryTokens;
        if (reattachedPlan is not null)
        {
            total += ContextTokenEstimator.EstimateMessage(
                reattachedPlan,
                cfg.IncludeReasoningInModelContext,
                cfg.RequestHistoryHygiene);
        }

        if (reattachedUser is not null)
        {
            total += ContextTokenEstimator.EstimateMessage(
                reattachedUser,
                cfg.IncludeReasoningInModelContext,
                cfg.RequestHistoryHygiene);
        }

        total += ContextTokenEstimator.Estimate(
            tail,
            cfg.IncludeReasoningInModelContext,
            maxToolScreenshots: cfg.MaxToolScreenshotsInModelContext,
            hygiene: cfg.RequestHistoryHygiene);
        return total;
    }
}
