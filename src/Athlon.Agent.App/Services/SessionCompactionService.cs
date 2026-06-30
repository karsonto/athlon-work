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
    IAgentEnvironmentPromptBuilder environmentPromptBuilder,
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
        var environmentPrompt = environmentPromptBuilder.Build(session, tools);
        var runContext = AgentRunContext.CreateRoot(
            session,
            Guid.NewGuid().ToString("N"),
            toolRouter,
            promptOrchestrator,
            ResolveIgnorePatterns(session));

        var invocation = new AgentTurnInvocation
        {
            RunContext = runContext,
            Session = session,
            StreamAdapter = new AgentStreamAdapter(session.Id, runContext.RunId),
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

    private IReadOnlyList<string> ResolveIgnorePatterns(AgentSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var fullPath = Path.GetFullPath(session.ActiveWorkspace);
            var match = settings.Workspaces.FirstOrDefault(workspace =>
                !string.IsNullOrWhiteSpace(workspace.RootPath)
                && string.Equals(Path.GetFullPath(workspace.RootPath), fullPath, StringComparison.OrdinalIgnoreCase));
            return WorkspaceIgnoreResolver.Resolve(
                workspacePatterns: match?.IgnorePatterns,
                globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
        }

        var configuredWorkspace = settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        return WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: configuredWorkspace?.IgnorePatterns,
            globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
    }
}
