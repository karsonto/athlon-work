using System.Net.Http;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core;

internal sealed class AgentTurnCoordinator(
    IAgentModelClient modelClient,
    ITokenEstimatorCalibrator tokenEstimatorCalibrator,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IPromptPressureStore promptPressureStore,
    AppSettings settings,
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
        int contextSavingsTokens,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await modelClient.CompleteAsync(
                new AgentModelRequest(modelMessages, tools),
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

            environmentPrompt = resolveSystemPromptOrchestrator().BuildForReasoningIteration(
                frozenPrompt,
                session,
                tools);
            var retryMessages = ModelMessageBuilder.BuildForSession(
                environmentPrompt,
                session.Messages,
                settings.ContextCompaction.IncludeReasoningInModelContext);
            var response = await modelClient.CompleteAsync(
                new AgentModelRequest(retryMessages, tools),
                token => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnTextDelta(assistantMessageId, token)),
                token => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnReasoningDelta(assistantMessageId, token)),
                delta => AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnToolCallDelta(assistantMessageId, delta)),
                cancellationToken);
            await RecordModelUsageAsync(session, callbacks, environmentPrompt, tools, response, contextSavingsTokens: 0);
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
        if (callbacks?.OnUsageRecorded is { } onUsage)
        {
            await onUsage(snapshot);
        }

        await AgentRuntime.PublishStreamEventsAsync(
            callbacks,
            [new AgentStreamEvent.UsageRecorded(snapshot)]);
    }
}
