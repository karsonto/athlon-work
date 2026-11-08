using System.Text;
using System.Text.Json;
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
    IFileStorageService storage,
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

        // Leaders always use fixed orchestration tools — ignore any stale profile allow-list
        // from older configs that may have included file/MCP/skills.
        var allowList = isLeader
            ? TeamToolDefaults.DefaultLeaderAllowList
            : TeamToolDefaults.ResolveAllowList(profile, isLeader: false);
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

        var rootSession = await storage.LoadSessionAsync(task.RootSessionId, cancellationToken)
            .ConfigureAwait(false);

        var memberDir = taskStore.ResolveMemberDirectory(task.RootSessionId, task.TaskId, memberSessionId);
        Directory.CreateDirectory(memberDir);

        var sessionPath = Path.Combine(memberDir, "session.json");
        AgentSession session;
        if (File.Exists(sessionPath))
        {
            session = await jsonFileStore.LoadAsync<AgentSession>(sessionPath, cancellationToken).ConfigureAwait(false)
                ?? CreateMemberSession(task, memberSessionId, profile, rootSession);
        }
        else
        {
            session = CreateMemberSession(task, memberSessionId, profile, rootSession);
        }

        session = InheritWorkspaceFromRoot(session, rootSession);

        if (!string.IsNullOrWhiteSpace(profile.ModelNameOverride))
        {
            session = session with { ModelName = profile.ModelNameOverride };
        }

        var ignorePatterns = WorkspaceSessionResolver.ResolveIgnorePatterns(session, settings);
        var workspaceKind = runContextAccessor.Current?.WorkspaceKind
            ?? WorkspaceSessionResolver.ResolveKind(session, settings);
        var workspaceRoot = WorkspaceSessionResolver.NormalizeRoot(session, settings)
            ?? runContextAccessor.Current?.WorkspaceRoot;

        var parentSeed = rootSession?.WithWorkspace(session.ActiveWorkspace, session.ActiveWorkspaceId)
            ?? new AgentSession(
                task.RootSessionId,
                string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                session.ActiveWorkspace,
                null,
                settings.Model.ModelName,
                [])
            {
                ActiveWorkspaceId = session.ActiveWorkspaceId
            };

        var parentContext = runContextAccessor.Current
            ?? AgentRunContext.CreateRoot(
                parentSeed,
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
        // Knowledge must be written after Push so ResolveSessionDirectory targets
        // teams/.../members/... rather than a top-level sessions/{memberId} folder.
        await EnsureKnowledgeAsync(memberSessionId, profile, task.RootSessionId, cancellationToken).ConfigureAwait(false);
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

        var lastPersistMs = 0L;
        var lastMessageCount = session.Messages.Count;
        var callbacks = new AgentTurnCallbacks
        {
            OnSessionUpdated = async updated =>
            {
                var countChanged = updated.Messages.Count != lastMessageCount;
                session = updated;
                lastMessageCount = updated.Messages.Count;
                var now = Environment.TickCount64;
                // Always flush when a new message/tool row appears; throttle pure content churn.
                if (!countChanged && now - lastPersistMs < 400)
                {
                    return;
                }

                lastPersistMs = now;
                try
                {
                    // Write directly under the member directory so Trace Tools can poll mid-turn
                    // without depending on SessionRuntimeStore path/hydration for nested sessions.
                    await PersistMemberTranscriptAsync(memberDir, sessionPath, session, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort checkpoint; final save still happens below.
                }
            }
        };

        try
        {
            var orchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
            session = await orchestrator.SendAsync(session, message.Trim(), null, callbacks, cancellationToken)
                .ConfigureAwait(false);
            await PersistMemberTranscriptAsync(memberDir, sessionPath, session, cancellationToken)
                .ConfigureAwait(false);

            var responseText = SubAgentResultFormatter.ExtractLastAssistantText(session);
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return TeamMemberRunOutcome.Fail("Team member finished without assistant text.");
            }

            var parsed = TeamMemberResultParser.Parse(responseText);
            if (parsed.Status == TeamMemberResultStatus.Invalid)
            {
                return TeamMemberRunOutcome.Fail(
                    "Worker output rejected (empty, preamble-only, or invalid). Produce a full ## Result with status.");
            }

            if (!TeamMemberResultParser.IsSuccessful(parsed))
            {
                // Surface structured non-success so ApplyWorkerResultAsync can mark Failed
                // while preserving the full response for Leader collect / memory.
                return new TeamMemberRunOutcome
                {
                    Ok = false,
                    ResponseText = responseText,
                    Error = $"status={parsed.Status}; verdict={parsed.Verdict ?? "-"}; {TeamWorkerHelpers.Truncate(parsed.FindingsSummary ?? responseText, 500)}"
                };
            }

            return TeamMemberRunOutcome.Success(responseText, session);
        }
        catch (OperationCanceledException)
        {
            await PersistMemberTranscriptAsync(memberDir, sessionPath, session, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Team member run failed task={TaskId} member={MemberId}", task.TaskId, memberSessionId);
            return TeamMemberRunOutcome.Fail(ex.Message);
        }
    }

    private async Task PersistMemberTranscriptAsync(
        string memberDir,
        string sessionPath,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(memberDir);
        await jsonFileStore.SaveAsync(sessionPath, session, cancellationToken).ConfigureAwait(false);

        // Snapshot conversation.jsonl for Trace Tools polling. Prefer a full rewrite so mid-turn
        // Upserts from SessionRuntimeStore (possibly under a different path) cannot leave a stale log.
        var conversationPath = Path.Combine(memberDir, "conversation.jsonl");
        var builder = new StringBuilder(Math.Max(256, session.Messages.Count * 128));
        foreach (var message in session.Messages)
        {
            if (message.Role is MessageRole.System or MessageRole.Compaction)
            {
                continue;
            }

            builder.AppendLine(JsonSerializer.Serialize(message, JsonFileStore.JsonLineOptions));
        }

        await File.WriteAllTextAsync(conversationPath, builder.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
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

    private AgentSession CreateMemberSession(
        TeamTask task,
        string memberSessionId,
        AgentProfile profile,
        AgentSession? rootSession)
    {
        var workspace = rootSession?.ActiveWorkspace
            ?? runContextAccessor.Current?.WorkspaceRoot
            ?? SessionWorkspaceScope.CurrentState?.RootPath;
        return new AgentSession(
            memberSessionId,
            string.Empty,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            workspace,
            null,
            profile.ModelNameOverride ?? settings.Model.ModelName,
            [])
        {
            ActiveWorkspaceId = rootSession?.ActiveWorkspaceId
        };
    }

    /// <summary>
    /// Keep Team member sessions aligned with the Chat root session workspace (path + id),
    /// matching SubAgent inheritance. Falls back to ambient run/scope workspace when the root
    /// session has none.
    /// </summary>
    private AgentSession InheritWorkspaceFromRoot(AgentSession member, AgentSession? rootSession)
    {
        var workspace = rootSession?.ActiveWorkspace
            ?? runContextAccessor.Current?.WorkspaceRoot
            ?? SessionWorkspaceScope.CurrentState?.RootPath;
        var workspaceId = rootSession?.ActiveWorkspaceId;

        if (string.IsNullOrWhiteSpace(workspace) && string.IsNullOrWhiteSpace(workspaceId))
        {
            return member;
        }

        if (string.Equals(member.ActiveWorkspace, workspace, StringComparison.OrdinalIgnoreCase)
            && string.Equals(member.ActiveWorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
        {
            return member;
        }

        return member.WithWorkspace(workspace, workspaceId);
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
        if (isLeader)
        {
            sb.AppendLine(
                "You are the Team Leader. Use team_* to list members, create subtasks, dispatch, collect, and submit. "
                + "After team_dispatch, STOP — do not poll with team_progress/team_await; the outer loop re-invokes you when workers finish. "
                + "When a verification worker fails, redispatch the implementer (or create a fix subtask) with findings, then re-verify. "
                + "There is no per-worker iteration cap; stop only when global repair rounds are exhausted, then ask_user or submit partial/failed. "
                + "Use team_progress / team_memory for shared state.");
        }
        else
        {
            sb.AppendLine(
                "You are a Worker with an independent context. Do not call team_dispatch, team_submit_result, team_create_subtask, or sessions_*. "
                + "You may call team_progress and team_memory. Deliver a complete result — do not end with a preamble-only plan.");
        }

        sb.AppendLine();
        sb.AppendLine("## Result");
        sb.AppendLine("status: completed | failed | blocked | needs_leader");
        sb.AppendLine("verdict: pass | fail   # optional; for verification workers");
        sb.AppendLine("fix_target_subtask_id: sub_xxx   # optional when verdict=fail");
        sb.AppendLine("accomplished:");
        sb.AppendLine("- ...");
        sb.AppendLine("findings:");
        sb.AppendLine("- ...");
        return sb.ToString();
    }
}
