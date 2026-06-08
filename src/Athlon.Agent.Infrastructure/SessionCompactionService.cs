using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.ComposerCommands;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure;

public sealed class SessionCompactionService(
    IPreCompletionPipeline preCompletionPipeline,
    IToolRouter toolRouter,
    ISystemPromptOrchestrator systemPromptOrchestrator,
    ITokenEstimatorCalibrator tokenEstimatorCalibrator,
    IFileStorageService storage,
    AppSettings settings) : ISessionCompactionService
{
    public async Task<SessionCompactionResult> CompactManuallyAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        var activeRouter = AmbientToolRouterScope.CurrentRouter ?? toolRouter;
        var activePrompt = AmbientSystemPromptOrchestratorScope.CurrentOrchestrator ?? systemPromptOrchestrator;
        var tools = activeRouter.ListTools();
        var frozenPrompt = activePrompt.PrepareForTurn(session, tools);
        var environmentPrompt = activePrompt.BuildForReasoningIteration(frozenPrompt, session, tools);

        CompactionRuntimeContext? runtimeContext = null;
        if (settings.ContextCompaction.DynamicCompaction.Enabled)
        {
            var multiplier = tokenEstimatorCalibrator.GetMultiplier(session.Id);
            var budget = ContextBudgetCalculator.Compute(
                environmentPrompt,
                tools,
                session.Messages,
                settings.ContextCompaction,
                settings.Model,
                multiplier);
            runtimeContext = new CompactionRuntimeContext(
                budget,
                environmentPrompt,
                tools,
                multiplier);
        }

        var messageIdsBefore = session.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        var updated = await preCompletionPipeline.RunAsync(
            session,
            PreCompletionOptions.ManualForceCompact,
            runtimeContext,
            cancellationToken);
        updated = await PersistCompactionAuditsAsync(updated, messageIdsBefore, callbacks, cancellationToken);

        var compacted = DetectManualCompaction(messageIdsBefore, updated.Messages);
        var status = compacted
            ? "已手动压缩上下文。"
            : "当前上下文无需压缩。";

        return new SessionCompactionResult(updated, compacted, status);
    }

    private static bool DetectManualCompaction(
        HashSet<string> messageIdsBefore,
        IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (messageIdsBefore.Contains(message.Id))
            {
                continue;
            }

            if (message.Role == MessageRole.Compaction
                && message.Content.Contains("manualcompact", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return messages.Count < messageIdsBefore.Count;
    }

    private async Task<AgentSession> PersistCompactionAuditsAsync(
        AgentSession session,
        HashSet<string> messageIdsBefore,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        if (HasCompactionStructureChange(session, messageIdsBefore))
        {
            if (callbacks?.OnSessionUpdated is not null)
            {
                await callbacks.OnSessionUpdated(session);
            }
        }

        var persistedNew = false;
        foreach (var message in session.Messages)
        {
            if (messageIdsBefore.Contains(message.Id))
            {
                continue;
            }

            persistedNew = true;
            if (callbacks?.OnStreamEvent is not null)
            {
                await callbacks.OnStreamEvent(new AgentStreamEvent.ChatMessageAppended(message));
            }

            await storage.AppendConversationMessageAsync(session.Id, message, cancellationToken);
            await storage.SaveSessionAsync(session, cancellationToken);
        }

        if (!persistedNew)
        {
            await storage.SaveSessionAsync(session, cancellationToken);
        }

        return session;
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

        return session.Messages.Count < messageIdsBefore.Count;
    }
}
