using System.Net.Http;
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
    AppSettings settings,
    IAgentRunContextAccessor runContextAccessor,
    Func<ISystemPromptOrchestrator> resolveSystemPromptOrchestrator,
    Func<AgentSession, AgentTurnCallbacks?, PreCompletionOptions, string, IReadOnlyList<ToolDefinition>, ContextPressureLevel, CancellationToken, Task<AgentSession>> runPreCompletionPipelineAsync,
    IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForContext("AgentTurnCoordinator");

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
        CancellationToken cancellationToken)
    {
        try
        {
            var allowToolCalls = ScheduleTurnScope.Current?.AllowToolCalls ?? true;
            var response = await modelClient.CompleteAsync(
                new AgentModelRequest(modelMessages, tools, AllowToolCalls: allowToolCalls),
                token => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnTextDelta(assistantMessageId, token)),
                token => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnReasoningDelta(assistantMessageId, token)),
                delta => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnToolCallDelta(assistantMessageId, delta)),
                cancellationToken);
            await RecordModelUsageAsync(session, callbacks, environmentPrompt, tools, response, contextSavingsTokens);
            return (session, response);
        }
        catch (HttpRequestException ex) when (AgentRuntime.IsContextLengthError(ex))
        {
            _logger.Warning("Context length exceeded for session {SessionId}; forcing compact and retrying once", session.Id);

            session = await runPreCompletionPipelineAsync(
                session,
                callbacks,
                PreCompletionOptions.ForceCompact,
                environmentPrompt,
                tools,
                ContextPressureLevel.Overflow,
                cancellationToken);

            modelMessageCache?.Invalidate();
            environmentPrompt = resolveSystemPromptOrchestrator().BuildForReasoningIteration(
                frozenPrompt,
                session,
                tools);
            var retryResult = ModelMessagesForApiBuilder.Build(
                modelMessageCache,
                environmentPrompt,
                session.Messages,
                settings.ContextCompaction);
            var allowToolCalls = ScheduleTurnScope.Current?.AllowToolCalls ?? true;
            var response = await modelClient.CompleteAsync(
                new AgentModelRequest(retryResult.Messages, tools, AllowToolCalls: allowToolCalls),
                token => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnTextDelta(assistantMessageId, token)),
                token => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnReasoningDelta(assistantMessageId, token)),
                delta => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnToolCallDelta(assistantMessageId, delta)),
                cancellationToken);
            await RecordModelUsageAsync(session, callbacks, environmentPrompt, tools, response, retryResult.EstimatedSavingsTokens);
            return (session, response);
        }
    }

    private async Task RecordModelUsageAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        string environmentPrompt,
        IReadOnlyList<ToolDefinition> tools,
        AgentModelResponse response,
        int contextSavingsTokens)
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
            multiplier);
        var estimatedPromptTokens = budget.FixedOverhead + budget.EstimatedHistory;
        tokenEstimatorCalibrator.Observe(session.Id, estimatedPromptTokens, response.Usage.PromptTokens);
        promptPressureStore.Record(session.Id, response.Usage.PromptTokens.Value);

        var snapshot = sessionUsageAccumulator.Record(session.Id, response.Usage, contextSavingsTokens);
        var parentSessionId = runContextAccessor.Current?.ParentSessionId;
        if (parentSessionId is not null)
        {
            sessionUsageAccumulator.RecordRollup(parentSessionId, response.Usage, contextSavingsTokens);
            snapshot = sessionUsageAccumulator.Get(parentSessionId);
        }

        if (callbacks?.OnUsageRecorded is { } onUsage)
        {
            await onUsage(snapshot);
        }

        var events = new List<AgentStreamEvent> { new AgentStreamEvent.UsageRecorded(snapshot) };
        if (contextSavingsTokens > 0)
        {
            events.Add(new AgentStreamEvent.ContextHygieneApplied(contextSavingsTokens));
        }

        await AgentRuntime.PublishStreamEventsAsync(callbacks, events);
    }
}
