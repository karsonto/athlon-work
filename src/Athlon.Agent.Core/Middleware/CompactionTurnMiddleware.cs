using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core.Middleware;

public sealed class CompactionTurnMiddleware(
    IPreCompletionPipeline preCompletionPipeline,
    ITokenEstimatorCalibrator tokenEstimatorCalibrator,
    IPromptPressureStore promptPressureStore,
    IFileStorageService storage,
    AppSettings settings,
    IConversationTranscriptWriter? transcriptWriter = null) : AgentTurnMiddlewareBase
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
        var multiplier = tokenEstimatorCalibrator.GetMultiplier(invocation.Session.Id);
        var promptOccupancy = invocation.FrozenPrompt?.Occupancy;
        var lastPromptTokens = promptPressureStore.GetLastPromptTokens(invocation.Session.Id);
        var budget = ContextBudgetResolver.Resolve(
            environmentPrompt,
            tools,
            invocation.Session.Messages,
            settings.ContextCompaction,
            settings.Model,
            multiplier,
            invocation.RuntimeContext,
            promptOccupancy,
            lastPromptTokens);
        var rawHistoryEstimate = Math.Abs(multiplier - 1.0) < 0.001
            ? budget.EstimatedHistory
            : ContextBudgetCalculator.EstimateRawHistory(
                invocation.Session.Messages,
                settings.ContextCompaction);
        var pressure = ContextPressureEvaluator.Evaluate(
            budget,
            compaction.DynamicCompaction,
            forceOverflow: pressureOverride == ContextPressureLevel.Overflow);
        await PublishBudgetAsync(invocation, budget, pressure).ConfigureAwait(false);

        if (compaction.Enabled || options.ForceConversationCompact)
        {
            runtimeContext = new CompactionRuntimeContext(
                budget,
                environmentPrompt,
                tools,
                multiplier,
                pressureOverride,
                lastPromptTokens,
                rawHistoryEstimate);
        }

        invocation.CompactionContext = runtimeContext;
        invocation.State.Compaction = runtimeContext;
        var historyBeforeCompaction = invocation.Session.Messages;
        var messageIdsBefore = invocation.Session.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        invocation.Session = await preCompletionPipeline.RunAsync(
            invocation.Session,
            options,
            runtimeContext,
            cancellationToken).ConfigureAwait(false);
        invocation.Session = await PersistCompactionAuditsAsync(invocation, messageIdsBefore, cancellationToken)
            .ConfigureAwait(false);

        // The stored measurement describes the payload as it was *before* compaction. Keeping it
        // would let ApplyPromptPressure push the freshly reduced estimate back up, so the occupancy
        // meter and pressure level would never fall. Drop it; the next real API response records the
        // new, smaller value.
        if (DidCompact(historyBeforeCompaction, invocation.Session.Messages))
        {
            promptPressureStore.Clear(invocation.Session.Id);
        }

        var afterBudget = ContextBudgetResolver.Resolve(
            environmentPrompt,
            tools,
            invocation.Session.Messages,
            settings.ContextCompaction,
            settings.Model,
            multiplier,
            invocation.RuntimeContext,
            promptOccupancy,
            promptPressureStore.GetLastPromptTokens(invocation.Session.Id));
        var afterPressure = ContextPressureEvaluator.Evaluate(
            afterBudget,
            compaction.DynamicCompaction,
            forceOverflow: pressureOverride == ContextPressureLevel.Overflow);
        await PublishBudgetAsync(invocation, afterBudget, afterPressure).ConfigureAwait(false);
        return invocation.Session;
    }

    /// <summary>
    /// True when this round rewrote the payload in place or replaced part of it with a summary:
    /// that is exactly when the stored prompt measurement stops describing the request.
    /// Covers in-place rewrites too (truncate-args, prefix re-eviction), which replace message
    /// content without changing ids.
    /// </summary>
    private static bool DidCompact(
        IReadOnlyList<ChatMessage> before,
        IReadOnlyList<ChatMessage> after)
    {
        if (after.Count == 0)
        {
            return false;
        }

        var beforeById = before.ToDictionary(message => message.Id, StringComparer.Ordinal);
        var afterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in after)
        {
            afterIds.Add(message.Id);
            if (!beforeById.TryGetValue(message.Id, out var original))
            {
                return true;
            }

            if (!string.Equals(message.Content, original.Content, StringComparison.Ordinal)
                || !string.Equals(message.ToolCallsJson, original.ToolCallsJson, StringComparison.Ordinal)
                || !string.Equals(message.ReasoningContent, original.ReasoningContent, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return before.Any(message => !afterIds.Contains(message.Id));
    }

    private static Task PublishBudgetAsync(
        AgentTurnInvocation invocation,
        ContextBudgetSnapshot budget,
        ContextPressureLevel pressure) =>
        AgentRuntime.PublishStreamEventsAsync(
            invocation.Callbacks,
            [new AgentStreamEvent.ContextBudgetUpdated(budget, pressure)]);

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

        if (transcriptWriter is not null)
        {
            await transcriptWriter.FlushSessionAsync(invocation.Session.Id, cancellationToken)
                .ConfigureAwait(false);
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
            if (transcriptWriter is not null)
            {
                await transcriptWriter.AppendAsync(invocation.Session.Id, message, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await storage.AppendConversationMessageAsync(invocation.Session.Id, message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (transcriptWriter is not null)
        {
            await transcriptWriter.MarkSessionDirtyAsync(invocation.Session, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await storage.SaveSessionAsync(invocation.Session, cancellationToken).ConfigureAwait(false);
        }

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
