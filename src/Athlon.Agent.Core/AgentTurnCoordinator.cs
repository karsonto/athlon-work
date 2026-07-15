using System.Net.Http;
using System.Diagnostics;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.SubAgents;

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
    IEventManager? eventManager = null)
{
    private readonly IAppLogger _logger = logger.ForContext("AgentTurnCoordinator");
    private readonly IEventManager _eventManager = eventManager ?? NullEventManager.Instance;

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
            _logger.Warning("Context length exceeded for session {SessionId}; forcing compact and retrying once", session.Id);
            _eventManager.Record(
                BehaviorEventIds.Context,
                BehaviorEventTypes.Event,
                BehaviorEventIds.Context,
                new Dictionary<string, object?>
                {
                    ["action"] = "overflow_retry",
                    ["session_id"] = session.Id
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
            var allowToolCalls = ScheduleTurnScope.Current?.AllowToolCalls ?? true;
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
            throw;
        }
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
