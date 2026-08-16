using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

public sealed record ManualCompactionResult(
    AgentSession Session,
    bool Compacted,
    string? StatusMessage = null);

public sealed class SessionCompactionService(
    CompactionTurnMiddleware compactionMiddleware,
    IToolRouter toolRouter,
    ISystemPromptOrchestrator promptOrchestrator,
    AppSettings settings)
{
    public async Task<ManualCompactionResult> CompactAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        var conversation = session.Messages
            .Where(message => message.Role != MessageRole.Compaction)
            .ToList();
        if (conversation.Count == 0)
        {
            return new ManualCompactionResult(session, false);
        }

        var messageIdsBefore = session.Messages
            .Select(message => message.Id)
            .ToHashSet(StringComparer.Ordinal);

        var tools = toolRouter.ListTools();
        var frozen = promptOrchestrator.PrepareForTurn(session, tools);
        var environmentPrompt = frozen.Text;
        var runContext = AgentRunContext.CreateRoot(
            session,
            Guid.NewGuid().ToString("N"),
            toolRouter,
            promptOrchestrator,
            ResolveIgnorePatterns(session),
            WorkspaceSessionResolver.ResolveKind(session, settings));

        var invocation = new AgentTurnInvocation
        {
            RunContext = runContext,
            Session = session,
            StreamAdapter = new AgentStreamAdapter(session.Id, runContext.RunId),
            FrozenPrompt = frozen,
            EnvironmentPrompt = environmentPrompt,
            Tools = tools
        };

        var compactedSession = await compactionMiddleware.RunPreCompletionAsync(
            invocation,
            PreCompletionOptions.ManualCompact,
            environmentPrompt,
            tools,
            cancellationToken,
            ContextPressureLevel.Critical).ConfigureAwait(false);

        var compacted = HasCompactionStructureChange(compactedSession, messageIdsBefore);
        return new ManualCompactionResult(compactedSession, compacted);
    }

    public ContextBudgetSnapshot ComputeBudget(AgentSession session)
    {
        var tools = toolRouter.ListTools();
        var frozen = promptOrchestrator.PrepareForTurn(session, tools);
        var runtimeContext = promptOrchestrator.BuildRuntimeContext(session, tools);
        return ContextBudgetCalculator.Compute(
            frozen.Text,
            tools,
            session.Messages,
            settings.ContextCompaction,
            settings.Model,
            calibrationMultiplier: 1.0,
            runtimeContext: runtimeContext,
            promptOccupancy: frozen.Occupancy);
    }

    private static bool HasCompactionStructureChange(AgentSession session, HashSet<string> messageIdsBefore)
    {
        foreach (var message in session.Messages)
        {
            if (!messageIdsBefore.Contains(message.Id)
                && (message.Role == MessageRole.Compaction
                    || SummaryMessageBuilder.IsSummaryMessage(message)))
            {
                return true;
            }
        }

        var messageIdsAfter = session.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        return messageIdsAfter.Count != messageIdsBefore.Count;
    }

    private IReadOnlyList<string> ResolveIgnorePatterns(AgentSession session) =>
        WorkspaceSessionResolver.ResolveIgnorePatterns(session, settings);
}
