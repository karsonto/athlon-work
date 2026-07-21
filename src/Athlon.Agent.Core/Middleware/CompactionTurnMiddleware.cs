using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core.Middleware;

public sealed class CompactionTurnMiddleware(
    IPreCompletionPipeline preCompletionPipeline,
    ITokenEstimatorCalibrator tokenEstimatorCalibrator,
    IPromptPressureStore promptPressureStore,
    IFileStorageService storage,
    AppSettings settings) : AgentTurnMiddlewareBase
{
    public override async ValueTask OnBeforeModelRoundAsync(
        AgentTurnInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.EnvironmentPrompt is null || invocation.Tools is null)
        {
            return;
        }

        var historyBeforePreCompletion = invocation.Session.Messages;
        invocation.Session = await RunPreCompletionAsync(
            invocation,
            PreCompletionOptions.AgentLoop,
            invocation.EnvironmentPrompt,
            invocation.Tools,
            cancellationToken).ConfigureAwait(false);
        invocation.ModelMessageCache?.NotePreCompletionResult(historyBeforePreCompletion, invocation.Session.Messages);
    }

    public async Task<AgentSession> RunPreCompletionAsync(
        AgentTurnInvocation invocation,
        PreCompletionOptions options,
        string environmentPrompt,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken,
        ContextPressureLevel pressureOverride = ContextPressureLevel.Normal)
    {
        CompactionRuntimeContext? runtimeContext = null;
        var compaction = settings.ContextCompaction;
        if (compaction.Enabled || options.ForceConversationCompact)
        {
            var multiplier = tokenEstimatorCalibrator.GetMultiplier(invocation.Session.Id);
            var budget = ContextBudgetCalculator.Compute(
                environmentPrompt,
                tools,
                invocation.Session.Messages,
                settings.ContextCompaction,
                settings.Model,
                multiplier,
                invocation.RuntimeContext);
            // Capture raw (uncalibrated) history before prompt-pressure may raise EstimatedHistory.
            var rawHistoryEstimate = Math.Abs(multiplier - 1.0) < 0.001
                ? budget.EstimatedHistory
                : ContextBudgetCalculator.EstimateRawHistory(
                    invocation.Session.Messages,
                    settings.ContextCompaction);
            budget = ApplyPromptPressure(budget, invocation.Session.Id);
            runtimeContext = new CompactionRuntimeContext(
                budget,
                environmentPrompt,
                tools,
                multiplier,
                pressureOverride,
                promptPressureStore.GetLastPromptTokens(invocation.Session.Id),
                rawHistoryEstimate);
        }

        invocation.CompactionContext = runtimeContext;
        invocation.State.Compaction = runtimeContext;
        var messageIdsBefore = invocation.Session.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        invocation.Session = await preCompletionPipeline.RunAsync(
            invocation.Session,
            options,
            runtimeContext,
            cancellationToken).ConfigureAwait(false);
        return await PersistCompactionAuditsAsync(invocation, messageIdsBefore, cancellationToken).ConfigureAwait(false);
    }

    private ContextBudgetSnapshot ApplyPromptPressure(ContextBudgetSnapshot budget, string sessionId)
    {
        var lastPromptTokens = promptPressureStore.GetLastPromptTokens(sessionId);
        if (lastPromptTokens is not > 0)
        {
            return budget;
        }

        var historyFromActual = Math.Max(0, lastPromptTokens.Value - budget.FixedOverhead);
        if (historyFromActual <= budget.EstimatedHistory)
        {
            return budget;
        }

        return budget.WithHistoryEstimate(historyFromActual, budget.HistoryBudget);
    }

    private async Task<AgentSession> PersistCompactionAuditsAsync(
        AgentTurnInvocation invocation,
        HashSet<string> messageIdsBefore,
        CancellationToken cancellationToken)
    {
        if (HasCompactionStructureChange(invocation.Session, messageIdsBefore))
        {
            if (invocation.Callbacks?.OnSessionUpdated is { } onSessionUpdated)
            {
                await onSessionUpdated(invocation.Session).ConfigureAwait(false);
            }
        }

        foreach (var message in invocation.Session.Messages)
        {
            if (messageIdsBefore.Contains(message.Id))
            {
                continue;
            }

            await AgentRuntime.PublishStreamEventsAsync(
                invocation.Callbacks,
                [new AgentStreamEvent.ChatMessageAppended(message)]).ConfigureAwait(false);
            await storage.AppendConversationMessageAsync(invocation.Session.Id, message, cancellationToken)
                .ConfigureAwait(false);
        }

        await storage.SaveSessionAsync(invocation.Session, cancellationToken).ConfigureAwait(false);
        return invocation.Session;
    }

    private static bool HasCompactionStructureChange(AgentSession session, HashSet<string> messageIdsBefore)
    {
        var hasNewCompactionAudit = false;
        var hasNewSummaryPlaceholder = false;
        foreach (var message in session.Messages)
        {
            if (messageIdsBefore.Contains(message.Id))
            {
                continue;
            }

            if (message.Role == MessageRole.Compaction)
            {
                hasNewCompactionAudit = true;
            }
            else if (SummaryMessageBuilder.IsSummaryMessage(message))
            {
                hasNewSummaryPlaceholder = true;
            }
        }

        if (hasNewCompactionAudit || hasNewSummaryPlaceholder)
        {
            return true;
        }

        var messageIdsAfter = session.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return messageIdsAfter.Count != messageIdsBefore.Count;
    }
}
