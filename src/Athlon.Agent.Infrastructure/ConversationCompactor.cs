using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Athlon.Agent.Core.RuntimeDiagnostics;
using System.Diagnostics;

namespace Athlon.Agent.Infrastructure;

public sealed class ConversationCompactor(
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
            && request.Strategy == CompactionStrategy.ForceCompact
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

            summary = summaryResponse.Content.Trim();
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
                EstimateCompactedTokens(summaryMessage, reattachedUser, tail, cfg),
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
        // The cutoff can advance past the user message that opened the current turn. Re-attach it
        // verbatim after the summary so the active instruction survives the cut instead of relying
        // on the summary alone.
        if (reattachedUser is not null)
        {
            compactMessages.Add(reattachedUser);
        }

        compactMessages.AddRange(tail);
        var tokensAfterPreview = EstimateCompactedTokens(summaryMessage, reattachedUser, tail, cfg);

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

        var summaryRequest = BuildSummaryRequest(
            middle,
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
            summary = summaryResponse.Content.Trim();
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
        var compactMessages = new List<ChatMessage>(head.Count + tail.Count + 2);
        compactMessages.AddRange(head);
        compactMessages.Add(hiddenSummary);
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

    /// <summary>Token estimate of the payload actually written back after compaction.</summary>
    private static int EstimateCompactedTokens(
        ChatMessage summaryMessage,
        ChatMessage? reattachedUser,
        IReadOnlyList<ChatMessage> tail,
        ContextCompactionSettings cfg)
    {
        var messages = new List<ChatMessage>(tail.Count + 2) { summaryMessage };
        if (reattachedUser is not null)
        {
            messages.Add(reattachedUser);
        }

        messages.AddRange(tail);
        return ContextTokenEstimator.Estimate(
            messages,
            cfg.IncludeReasoningInModelContext,
            hygiene: cfg.RequestHistoryHygiene);
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
        bool computeAuditMetrics,
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

        summaryInputCharsBefore = null;
        summaryInputCharsAfter = null;
        hygieneSavingsEstimate = null;

        // The formatted text only feeds the compaction audit display; the model payload is
        // built from the structured messages below. Skip the whole formatting/hygiene pass
        // unless an audit is actually emitted (the middle-cut path always skips it).
        if (computeAuditMetrics)
        {
            var formatted = ConversationSummaryFormatter.FormatMessages(prefix);
            summaryInputCharsBefore = formatted.Length;
            summaryInputCharsAfter = formatted.Length;

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
