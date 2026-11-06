using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure.Knowledge;
using Athlon.Agent.Infrastructure.SubAgents;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Infrastructure.Teams;

public sealed class TeamMemberRunExecutor(
    AppSettings settings,
    IServiceProvider serviceProvider,
    ITeamTaskStore taskStore,
    ISessionKnowledgeState sessionKnowledgeState,
    IJsonFileStore jsonFileStore,
    TeamMemberSystemPromptOrchestrator promptOrchestrator,
    IActiveAgentSessionContext activeSessionContext,
    IAgentRunContextAccessor runContextAccessor,
    ISessionHarnessState sessionHarnessState,
    IDebugPhaseAccessor debugPhaseAccessor,
    IPlanPhaseAccessor planPhaseAccessor,
    WorkspaceGuard workspaceGuard,
    IBrowserWorkspaceState browserWorkspaceState,
    ITerminalWorkspaceState terminalWorkspaceState,
    IMcpRegistry mcpRegistry,
    IAppLogger logger) : ITeamMemberRunExecutor
{
    private readonly IAppLogger _logger = logger.ForContext("TeamMemberRunExecutor");

    public async Task<TeamMemberRunOutcome> ExecuteAsync(
        TeamTask task,
        AgentProfile profile,
        string memberSessionId,
        string message,
        bool isLeader,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return TeamMemberRunOutcome.Fail("Teams feature is disabled.");
        }

        var allowList = TeamToolDefaults.ResolveAllowList(profile, isLeader);
        var localTools = serviceProvider.GetServices<IAgentTool>();
        var router = new ProfileToolRouter(
            allowList,
            isLeader,
            localTools,
            mcpRegistry,
            settings,
            activeSessionContext,
            sessionKnowledgeState,
            sessionHarnessState,
            runContextAccessor,
            debugPhaseAccessor,
            planPhaseAccessor,
            workspaceGuard,
            browserWorkspaceState,
            terminalWorkspaceState,
            runtimeDiagnosticEventSink: serviceProvider.GetService<IRuntimeDiagnosticEventSink>(),
            logger: logger);

        var memberDir = taskStore.ResolveMemberDirectory(task.RootSessionId, task.TaskId, memberSessionId);
        Directory.CreateDirectory(memberDir);

        var sessionPath = Path.Combine(memberDir, "session.json");
        AgentSession session;
        if (File.Exists(sessionPath))
        {
            session = await jsonFileStore.LoadAsync<AgentSession>(sessionPath, cancellationToken).ConfigureAwait(false)
                ?? CreateMemberSession(task, memberSessionId, profile);
        }
        else
        {
            session = CreateMemberSession(task, memberSessionId, profile);
        }

        if (!string.IsNullOrWhiteSpace(profile.ModelNameOverride))
        {
            session = session with { ModelName = profile.ModelNameOverride };
        }

        await EnsureKnowledgeAsync(memberSessionId, profile, task.RootSessionId, cancellationToken).ConfigureAwait(false);

        var ignorePatterns = WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: null,
            globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
        var workspaceKind = runContextAccessor.Current?.WorkspaceKind
            ?? WorkspaceSessionResolver.ResolveKind(session, settings);
        var workspaceRoot = string.IsNullOrWhiteSpace(session.ActiveWorkspace)
            ? runContextAccessor.Current?.WorkspaceRoot
            : workspaceKind == WorkspaceKind.Ssh
                ? RemotePathNormalizer.NormalizeRoot(session.ActiveWorkspace)
                : Path.GetFullPath(session.ActiveWorkspace);

        var parentContext = runContextAccessor.Current
            ?? AgentRunContext.CreateRoot(
                new AgentSession(
                    task.RootSessionId,
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    workspaceRoot,
                    null,
                    settings.Model.ModelName,
                    []),
                IdGen.NewId(),
                router,
                promptOrchestrator,
                ignorePatterns,
                workspaceKind);

        var completePrompt = BuildCompletePrompt(profile, isLeader, task);
        var maxRounds = profile.MaxModelToolRounds
            ?? settings.Teams.MaxModelToolRounds;
        var childContext = parentContext.CreateTeamMember(
            memberSessionId,
            task.TaskId,
            profile.Id,
            router,
            promptOrchestrator,
            role: profile.Name,
            loopOptions: new AgentLoopOptions { MaxModelToolRounds = maxRounds },
            workspaceRoot,
            ignorePatterns,
            completeSystemPrompt: completePrompt);

        var profileOptions = new AgentProfileTurnOptions(
            ProfileId: profile.Id,
            ModelNameOverride: profile.ModelNameOverride,
            MaxModelToolRounds: maxRounds,
            SkillNames: profile.SkillNames,
            McpServerNames: profile.McpServerNames,
            ToolAllowList: allowList,
            KnowledgeModuleIds: profile.KnowledgeModuleIds,
            IsLeader: isLeader,
            TeamTaskId: task.TaskId,
            RootSessionId: task.RootSessionId);

        using var profileScope = AgentProfileTurnScope.Enter(profileOptions);
        using var runScope = runContextAccessor.Push(childContext);
        using var sessionScope = activeSessionContext.Enter(memberSessionId);
        using var workspaceScope = SessionWorkspaceScope.Enter(
            childContext.WorkspaceRoot,
            childContext.WorkspaceIgnorePatterns,
            childContext.WorkspaceKind);

        _logger.Information(
            "Starting team member run task={TaskId} profile={ProfileId} member={MemberId} leader={IsLeader}",
            task.TaskId,
            profile.Id,
            memberSessionId,
            isLeader);

        var callbacks = new AgentTurnCallbacks
        {
            OnSessionUpdated = updated =>
            {
                session = updated;
                return Task.CompletedTask;
            }
        };

        try
        {
            var orchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
            session = await orchestrator.SendAsync(session, message.Trim(), null, callbacks, cancellationToken)
                .ConfigureAwait(false);
            await jsonFileStore.SaveAsync(sessionPath, session, cancellationToken).ConfigureAwait(false);

            var responseText = SubAgentResultFormatter.ExtractLastAssistantText(session)
                ?? "(Team member finished without assistant text.)";
            return TeamMemberRunOutcome.Success(responseText, session);
        }
        catch (OperationCanceledException)
        {
            await jsonFileStore.SaveAsync(sessionPath, session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Team member run failed task={TaskId} member={MemberId}", task.TaskId, memberSessionId);
            return TeamMemberRunOutcome.Fail(ex.Message);
        }
    }

    private async Task EnsureKnowledgeAsync(
        string memberSessionId,
        AgentProfile profile,
        string rootSessionId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> moduleIds = profile.KnowledgeModuleIds;
        if (profile.InheritSessionKnowledge && moduleIds.Count == 0)
        {
            var rootSnapshot = sessionKnowledgeState.GetSnapshot(rootSessionId);
            if (rootSnapshot.ModuleIds.Count == 0)
            {
                await sessionKnowledgeState.LoadAsync(rootSessionId, cancellationToken).ConfigureAwait(false);
                rootSnapshot = sessionKnowledgeState.GetSnapshot(rootSessionId);
            }

            moduleIds = rootSnapshot.ModuleIds.ToArray();
        }

        if (moduleIds.Count == 0)
        {
            return;
        }

        await sessionKnowledgeState.SaveAsync(
            memberSessionId,
            new SessionKnowledgeSnapshot(
                Enabled: true,
                ModuleIds: new HashSet<string>(moduleIds, StringComparer.OrdinalIgnoreCase)),
            cancellationToken).ConfigureAwait(false);
    }

    private AgentSession CreateMemberSession(TeamTask task, string memberSessionId, AgentProfile profile)
    {
        var root = runContextAccessor.Current;
        var workspace = root?.WorkspaceRoot;
        return new AgentSession(
            memberSessionId,
            string.Empty,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            workspace,
            null,
            profile.ModelNameOverride ?? settings.Model.ModelName,
            []);
    }

    private static string BuildCompletePrompt(AgentProfile profile, bool isLeader, TeamTask task)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(profile.SoulMd))
        {
            sb.AppendLine(profile.SoulMd.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(profile.AgentMd))
        {
            sb.AppendLine(profile.AgentMd.Trim());
            sb.AppendLine();
        }

        if (sb.Length == 0)
        {
            sb.AppendLine($"You are team member '{profile.Name}'.");
            if (!string.IsNullOrWhiteSpace(profile.Description))
            {
                sb.AppendLine(profile.Description.Trim());
            }

            sb.AppendLine();
        }

        sb.AppendLine($"Team task goal: {task.Goal}");
        sb.AppendLine(isLeader
            ? "You are the Team Leader. Use team_* tools to create, dispatch, await, collect, and submit. You may redispatch a Worker only when AllowRedispatch permits and under MaxIterations."
            : "You are a Worker. Do not call team_* orchestration tools or sessions_*. End with ## Result.");
        sb.AppendLine();
        sb.AppendLine("## Result");
        sb.AppendLine("status: completed | blocked | needs_leader");
        sb.AppendLine("accomplished:");
        sb.AppendLine("- ...");
        return sb.ToString();
    }
}
