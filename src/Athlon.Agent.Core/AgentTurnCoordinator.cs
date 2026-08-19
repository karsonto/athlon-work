using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Core;

internal sealed class AgentTurnCoordinator(
    IAgentModelClient modelClient,
    ITokenEstimatorCalibrator tokenEstimatorCalibrator,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IPromptPressureStore promptPressureStore,
    IFileStorageService storage,
    AppSettings settings,
    IAgentRunContextAccessor runContextAccessor,
    Func<AgentSession, AgentTurnCallbacks?, PreCompletionOptions, string, string?, IReadOnlyList<ToolDefinition>, ContextPressureLevel, CancellationToken, Task<AgentSession>> runPreCompletionPipelineAsync,
    IAppLogger logger,
    IEventManager? eventManager = null,
    IRuntimeDiagnosticEventSink runtimeDiagnosticEventSink = null!)
{
    private readonly IAppLogger _logger = logger.ForContext("AgentTurnCoordinator");
    private readonly IEventManager _eventManager = eventManager ?? NullEventManager.Instance;
    private readonly IRuntimeDiagnosticEventSink _runtimeDiagnosticEventSink = runtimeDiagnosticEventSink;
    private static readonly ConcurrentDictionary<string, int> MiddleCutAttemptsByRun = new(StringComparer.Ordinal);

    public async Task<(AgentSession Session, AgentModelResponse Response)> CompleteWithOverflowRetryAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        AgentStreamAdapter streamAdapter,
        string assistantMessageId,
        IReadOnlyList<AgentModelMessage> modelMessages,
        IReadOnlyList<ToolDefinition> tools,
        FrozenSystemPrompt frozenPrompt,
        string environmentPrompt,
        ModelMessageCache? modelMessageCache,
        int contextSavingsTokens,
        string? runtimeContext,
        CancellationToken cancellationToken)
    {
        var initialAttemptId = Guid.NewGuid().ToString("N");
        try
        {
            var allowToolCalls = ScheduleTurnScope.Current?.AllowToolCalls ?? true;
            var request = new AgentModelRequest(modelMessages, tools, AllowToolCalls: allowToolCalls);
            var response = await CompleteRecordedAsync(
                session, callbacks, streamAdapter, assistantMessageId, request, environmentPrompt,
                runtimeContext, contextSavingsTokens, initialAttemptId, null, cancellationToken).ConfigureAwait(false);
            return (session, response);
        }
        catch (HttpRequestException ex) when (AgentRuntime.IsContextLengthError(ex))
        {
            var failedTokens = RequestHistoryHygiene.EstimatePayloadTokens(modelMessages);
            _logger.Warning("Context length exceeded for session {SessionId}; forcing compact before retry", session.Id);

            // Contract: 关键故障同时写结构化诊断事件，便于 diagnose_logs 回放归因。
            var context = runContextAccessor.Current;
            var evt = new RuntimeDiagnosticEvent(
                eventId: "",
                ts: default,
                sequence: 0,
                sessionId: session.Id,
                runId: context?.RunId ?? session.Id,
                turnId: null,
                attemptId: initialAttemptId,
                parentAttemptId: null,
                toolCallId: null,
                messageId: null,
                component: RuntimeDiagnosticComponent.Model,
                phase: RuntimeDiagnosticPhase.Request,
                eventType: "model.context_length_exceeded",
                severity: RuntimeDiagnosticSeverity.Error,
                errorCode: RuntimeDiagnosticErrorCodes.ModelContextLengthExceeded,
                message: ex.Message);
            await _runtimeDiagnosticEventSink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);

            _eventManager.Record(
                BehaviorEventIds.Context,
                BehaviorEventTypes.Event,
                BehaviorEventIds.Context,
                new Dictionary<string, object?>
                {
                    ["action"] = "overflow_retry",
                    ["session_id"] = session.Id,
                    ["failed_tokens"] = failedTokens
                });

            session = await runPreCompletionPipelineAsync(
                session,
                callbacks,
                PreCompletionOptions.ForceCompact,
                environmentPrompt,
                runtimeContext,
                tools,
                ContextPressureLevel.Overflow,
                cancellationToken).ConfigureAwait(false);

            modelMessageCache?.Invalidate();
            var retryResult = ModelMessagesForApiBuilder.Build(
                modelMessageCache,
                frozenPrompt.Text,
                session.Messages,
                settings.ContextCompaction,
                runtimeContext);
            var retryTokens = RequestHistoryHygiene.EstimatePayloadTokens(retryResult.Messages);
            var allowToolCalls = ScheduleTurnScope.Current?.AllowToolCalls ?? true;
            if (retryTokens >= failedTokens)
            {
                _logger.Warning(
                    "Overflow compact did not reduce payload for session {SessionId} (failed={FailedTokens}, retry={RetryTokens}); skipping retry",
                    session.Id,
                    failedTokens,
                    retryTokens);
                _eventManager.Record(
                    BehaviorEventIds.Context,
                    BehaviorEventTypes.Event,
                    BehaviorEventIds.Context,
                    new Dictionary<string, object?>
                    {
                        ["action"] = "overflow_retry_skipped",
                        ["reason"] = "payload_not_reduced",
                        ["session_id"] = session.Id,
                        ["failed_tokens"] = failedTokens,
                        ["retry_tokens"] = retryTokens
                    });
                var skipMultiplier = tokenEstimatorCalibrator.GetMultiplier(session.Id);
                var skipBudget = ContextBudgetCalculator.Compute(
                    environmentPrompt,
                    tools,
                    session.Messages,
                    settings.ContextCompaction,
                    settings.Model,
                    skipMultiplier,
                    runtimeContext);
                await AgentRuntime.PublishStreamEventsAsync(
                    callbacks,
                    [
                        new AgentStreamEvent.OverflowRetrySkipped(failedTokens, retryTokens, "payload_not_reduced"),
                        new AgentStreamEvent.ContextBudgetUpdated(skipBudget, ContextPressureLevel.Overflow)
                    ]).ConfigureAwait(false);
                var skipContext = runContextAccessor.Current;
                var skipEvt = new RuntimeDiagnosticEvent(
                    eventId: "",
                    ts: default,
                    sequence: 0,
                    sessionId: session.Id,
                    runId: skipContext?.RunId ?? session.Id,
                    turnId: null,
                    attemptId: initialAttemptId,
                    parentAttemptId: null,
                    toolCallId: null,
                    messageId: null,
                    component: RuntimeDiagnosticComponent.Compaction,
                    phase: RuntimeDiagnosticPhase.Persist,
                    eventType: "compaction.retry_skipped",
                    severity: RuntimeDiagnosticSeverity.Warning,
                    errorCode: RuntimeDiagnosticErrorCodes.CompactionRetrySkipped,
                    message: $"Retry skipped because compacted payload was not reduced (failed={failedTokens}, retry={retryTokens}).");
                await _runtimeDiagnosticEventSink.EnqueueAsync(skipEvt, CancellationToken.None).ConfigureAwait(false);
                if (!settings.ContextCompaction.MiddleCutOnRetrySkipped)
                {
                    throw;
                }

                var runId = skipContext?.RunId;
                if (!string.IsNullOrWhiteSpace(runId))
                {
                    var attempts = MiddleCutAttemptsByRun.GetOrAdd(runId, 0);
                    if (attempts >= Math.Max(1, settings.ContextCompaction.MiddleCutMaxPerRun))
                    {
                        throw;
                    }

                    MiddleCutAttemptsByRun[runId] = attempts + 1;
                }

                var middleCutSession = await runPreCompletionPipelineAsync(
                    session,
                    callbacks,
                    new PreCompletionOptions
                    {
                        AllowTruncateArgs = true,
                        AllowConversationCompact = true,
                        ForceConversationCompact = true,
                        EmitCompactionAudit = true,
                        Strategy = CompactionStrategy.MiddleCutOnRetrySkipped
                    },
                    environmentPrompt,
                    runtimeContext,
                    tools,
                    ContextPressureLevel.Overflow,
                    cancellationToken).ConfigureAwait(false);

                modelMessageCache?.Invalidate();
                var middleCutResult = ModelMessagesForApiBuilder.Build(
                    modelMessageCache,
                    frozenPrompt.Text,
                    middleCutSession.Messages,
                    settings.ContextCompaction,
                    runtimeContext);
                var middleRetryTokens = RequestHistoryHygiene.EstimatePayloadTokens(middleCutResult.Messages);
                if (middleRetryTokens >= failedTokens)
                {
                    throw;
                }

                var middleCutRequest = new AgentModelRequest(middleCutResult.Messages, tools, AllowToolCalls: allowToolCalls);
                var middleCutResponse = await CompleteRecordedAsync(
                    middleCutSession,
                    callbacks,
                    streamAdapter,
                    assistantMessageId,
                    middleCutRequest,
                    environmentPrompt,
                    runtimeContext,
                    middleCutResult.EstimatedSavingsTokens,
                    Guid.NewGuid().ToString("N"),
                    ParentAttemptId: initialAttemptId,
                    cancellationToken).ConfigureAwait(false);
                return (middleCutSession, middleCutResponse);
            }

            var request = new AgentModelRequest(retryResult.Messages, tools, AllowToolCalls: allowToolCalls);
            var response = await CompleteRecordedAsync(
                session, callbacks, streamAdapter, assistantMessageId, request, environmentPrompt,
                runtimeContext, retryResult.EstimatedSavingsTokens, Guid.NewGuid().ToString("N"),
                ParentAttemptId: initialAttemptId, cancellationToken).ConfigureAwait(false);
            return (session, response);
        }
    }

    private async Task<AgentModelResponse> CompleteRecordedAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        AgentStreamAdapter streamAdapter,
        string assistantMessageId,
        AgentModelRequest request,
        string environmentPrompt,
        string? runtimeContext,
        int contextSavingsTokens,
        string attemptId,
        string? ParentAttemptId,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await modelClient.CompleteAsync(
                request,
                token => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnTextDelta(assistantMessageId, token)),
                token => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnReasoningDelta(assistantMessageId, token)),
                delta => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnToolCallDelta(assistantMessageId, delta)),
                cancellationToken).ConfigureAwait(false);
            response = response with { Usage = ModelUsageAccounting.Resolve(request, response) };
            sw.Stop();
            await RecordModelUsageAsync(
                session, callbacks, environmentPrompt, runtimeContext, request.Tools, response,
                contextSavingsTokens, attemptId, ParentAttemptId, sw.ElapsedMilliseconds).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            var context = runContextAccessor.Current;
            var purpose = context?.ParentSessionId is null ? ModelCallPurpose.Chat : ModelCallPurpose.SubAgent;
            var promptTokens = ContextTokenEstimator.EstimateModelRequest(request);
            var failedUsage = new ModelUsage(promptTokens, 0, promptTokens);
            sessionUsageAccumulator.RecordCall(session.Id, attemptId, purpose, failedUsage);
            if (context?.ParentSessionId is { } parentSessionId)
            {
                sessionUsageAccumulator.RecordCall(
                    parentSessionId, attemptId, ModelCallPurpose.SubAgent, failedUsage, subAgentRollup: true);
            }
            await storage.AppendAttemptEventAsync(
                session.Id,
                new AgentAttemptEvent(
                    DateTimeOffset.UtcNow, attemptId, session.Id, context?.RunId ?? session.Id,
                    AgentAttemptKind.Model,
                    purpose,
                    null, ToolCatalogFingerprint.Compute(request.Tools), session.ModelName,
                    promptTokens, 0, "failure",
                    ex.GetType().Name, sw.ElapsedMilliseconds, ParentAttemptId),
                CancellationToken.None).ConfigureAwait(false);

            // 跟踪模型请求失败：用于 diagnose_logs 收敛根因（默认归类为 request_failed）。
            if (ex is not HttpRequestException httpEx || !AgentRuntime.IsContextLengthError(httpEx))
            {
                var mappedCode = ResolveModelFailureCode(ex);
                var evt = new RuntimeDiagnosticEvent(
                    eventId: "",
                    ts: default,
                    sequence: 0,
                    sessionId: session.Id,
                    runId: context?.RunId ?? session.Id,
                    turnId: null,
                    attemptId: attemptId,
                    parentAttemptId: ParentAttemptId,
                    toolCallId: null,
                    messageId: null,
                    component: RuntimeDiagnosticComponent.Model,
                    phase: RuntimeDiagnosticPhase.Request,
                    eventType: "model.request_failed",
                    severity: RuntimeDiagnosticSeverity.Error,
                    errorCode: mappedCode,
                    message: ex.Message);
                await _runtimeDiagnosticEventSink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static string ResolveModelFailureCode(Exception ex)
    {
        if (ex is JsonException)
        {
            var message = ex.Message ?? string.Empty;
            if (message.Contains("tool", StringComparison.OrdinalIgnoreCase)
                && message.Contains("argument", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeDiagnosticErrorCodes.ModelToolCallJsonInvalid;
            }

            return RuntimeDiagnosticErrorCodes.ModelResponseJsonInvalid;
        }

        return RuntimeDiagnosticErrorCodes.ModelRequestFailed;
    }

    private async Task RecordModelUsageAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        string environmentPrompt,
        string? runtimeContext,
        IReadOnlyList<ToolDefinition> tools,
        AgentModelResponse response,
        int contextSavingsTokens,
        string attemptId,
        string? parentAttemptId,
        long latencyMs)
    {
        if (response.Usage?.PromptTokens is not > 0)
        {
            return;
        }

        var multiplier = tokenEstimatorCalibrator.GetMultiplier(session.Id);
        var budget = ContextBudgetCalculator.Compute(
            environmentPrompt,
            tools,
            session.Messages,
            settings.ContextCompaction,
            settings.Model,
            multiplier,
            runtimeContext);
        var estimatedPromptTokens = budget.FixedOverhead + budget.EstimatedHistory;
        tokenEstimatorCalibrator.Observe(session.Id, estimatedPromptTokens, response.Usage.PromptTokens);
        promptPressureStore.Record(session.Id, response.Usage.PromptTokens.Value);

        var context = runContextAccessor.Current;
        var purpose = context?.ParentSessionId is null ? ModelCallPurpose.Chat : ModelCallPurpose.SubAgent;
        var snapshot = sessionUsageAccumulator.RecordCall(
            session.Id, attemptId, purpose, response.Usage, contextSavingsTokens);
        var parentSessionId = context?.ParentSessionId;
        if (parentSessionId is not null)
        {
            sessionUsageAccumulator.RecordCall(
                parentSessionId, attemptId, ModelCallPurpose.SubAgent, response.Usage,
                contextSavingsTokens, subAgentRollup: true);
            snapshot = sessionUsageAccumulator.Get(parentSessionId);
        }

        await storage.AppendAttemptEventAsync(
            session.Id,
            new AgentAttemptEvent(
                DateTimeOffset.UtcNow, attemptId, session.Id, context?.RunId ?? session.Id,
                AgentAttemptKind.Model, purpose, null, ToolCatalogFingerprint.Compute(tools),
                session.ModelName, response.Usage.PromptTokens ?? 0, response.Usage.CompletionTokens ?? 0,
                "success", null, latencyMs, parentAttemptId)).ConfigureAwait(false);

        if (callbacks?.OnUsageRecorded is { } onUsage)
        {
            await onUsage(snapshot).ConfigureAwait(false);
        }

        var events = new List<AgentStreamEvent> { new AgentStreamEvent.UsageRecorded(snapshot) };
        if (contextSavingsTokens > 0)
        {
            events.Add(new AgentStreamEvent.ContextHygieneApplied(contextSavingsTokens));
            _eventManager.Record(
                BehaviorEventIds.Context,
                BehaviorEventTypes.Event,
                BehaviorEventIds.Context,
                new Dictionary<string, object?>
                {
                    ["action"] = "hygiene",
                    ["session_id"] = session.Id,
                    ["estimated_savings_tokens"] = contextSavingsTokens
                });
        }

        await AgentRuntime.PublishStreamEventsAsync(callbacks, events).ConfigureAwait(false);
    }
}
