using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Core;

public sealed record AgentRunContext
{
    public required string SessionId { get; init; }

    public required string RunId { get; init; }

    public string? ParentSessionId { get; init; }

    public string? WorkspaceRoot { get; init; }

    public WorkspaceKind WorkspaceKind { get; init; } = WorkspaceKind.Local;

    public IReadOnlyList<string> WorkspaceIgnorePatterns { get; init; } = WorkspaceIgnoreDefaults.BuiltIn;

    public required IToolRouter ToolRouter { get; init; }

    public required ISystemPromptOrchestrator PromptOrchestrator { get; init; }

    public string? SubAgentRole { get; init; }

    /// <summary>
    /// When non-empty, sub-agent prompt assembly uses this as the sole complete system prompt.
    /// </summary>
    public string? CompleteSystemPrompt { get; init; }

    /// <summary>
    /// When true, <see cref="Prompt.RuntimeContextAssembler"/> returns no runtime context for this run.
    /// </summary>
    public bool SuppressRuntimeContext { get; init; }

    public AgentLoopOptions? LoopOptions { get; init; }

    public AgentRunKind Kind { get; init; } = AgentRunKind.Root;

    public bool ComputerUseActive { get; init; }

    /// <summary>Set for <see cref="AgentRunKind.TeamMember"/> runs.</summary>
    public string? TeamTaskId { get; init; }

    /// <summary>Team definition id; used with <see cref="IsTeamLeader"/> for stable Leader dirs.</summary>
    public string? TeamId { get; init; }

    /// <summary>When true, session files live under teams/_leaders/{TeamId}.</summary>
    public bool IsTeamLeader { get; init; }

    /// <summary>Set for <see cref="AgentRunKind.TeamMember"/> runs.</summary>
    public string? TeamProfileId { get; init; }

    public static AgentRunContext CreateRoot(
        AgentSession session,
        string runId,
        IToolRouter toolRouter,
        ISystemPromptOrchestrator promptOrchestrator,
        IReadOnlyList<string> ignorePatterns,
        WorkspaceKind workspaceKind = WorkspaceKind.Local,
        bool computerUseActive = false) =>
        new()
        {
            SessionId = session.Id,
            RunId = runId,
            WorkspaceRoot = string.IsNullOrWhiteSpace(session.ActiveWorkspace)
                ? null
                : workspaceKind == WorkspaceKind.Ssh
                    ? RemotePathNormalizer.NormalizeRoot(session.ActiveWorkspace)
                    : Path.GetFullPath(session.ActiveWorkspace),
            WorkspaceKind = workspaceKind,
            WorkspaceIgnorePatterns = ignorePatterns,
            ToolRouter = toolRouter,
            PromptOrchestrator = promptOrchestrator,
            Kind = AgentRunKind.Root,
            ComputerUseActive = computerUseActive,
            SuppressRuntimeContext = computerUseActive
        };

    public AgentRunContext CreateChild(
        string subSessionId,
        IToolRouter childRouter,
        ISystemPromptOrchestrator childPrompt,
        string role,
        AgentLoopOptions? loopOptions,
        string? workspaceRoot,
        IReadOnlyList<string> ignorePatterns,
        string? completeSystemPrompt = null) =>
        new()
        {
            SessionId = subSessionId,
            RunId = IdGen.NewId(),
            ParentSessionId = Kind == AgentRunKind.SubAgent && ParentSessionId is not null
                ? ParentSessionId
                : SessionId,
            WorkspaceRoot = workspaceRoot,
            WorkspaceKind = WorkspaceKind,
            WorkspaceIgnorePatterns = ignorePatterns,
            ToolRouter = childRouter,
            PromptOrchestrator = childPrompt,
            SubAgentRole = role,
            CompleteSystemPrompt = completeSystemPrompt,
            SuppressRuntimeContext = SuppressRuntimeContext,
            LoopOptions = loopOptions,
            Kind = AgentRunKind.SubAgent,
            ComputerUseActive = ComputerUseActive
        };

    public AgentRunContext CreateTeamMember(
        string memberSessionId,
        string teamTaskId,
        string profileId,
        IToolRouter memberRouter,
        ISystemPromptOrchestrator memberPrompt,
        string role,
        AgentLoopOptions? loopOptions,
        string? workspaceRoot,
        IReadOnlyList<string> ignorePatterns,
        string? completeSystemPrompt = null,
        string? teamId = null,
        bool isLeader = false) =>
        new()
        {
            SessionId = memberSessionId,
            RunId = IdGen.NewId(),
            ParentSessionId = (Kind is AgentRunKind.TeamMember or AgentRunKind.SubAgent) && ParentSessionId is not null
                ? ParentSessionId
                : SessionId,
            WorkspaceRoot = workspaceRoot,
            WorkspaceKind = WorkspaceKind,
            WorkspaceIgnorePatterns = ignorePatterns,
            ToolRouter = memberRouter,
            PromptOrchestrator = memberPrompt,
            SubAgentRole = role,
            CompleteSystemPrompt = completeSystemPrompt,
            SuppressRuntimeContext = SuppressRuntimeContext,
            LoopOptions = loopOptions,
            Kind = AgentRunKind.TeamMember,
            ComputerUseActive = ComputerUseActive,
            TeamTaskId = teamTaskId,
            TeamId = teamId,
            IsTeamLeader = isLeader,
            TeamProfileId = profileId
        };

    public string ResolveSessionDirectory(string sessionsPath, string sessionId)
    {
        if (Kind == AgentRunKind.TeamMember
            && ParentSessionId is not null
            && IsTeamLeader
            && !string.IsNullOrWhiteSpace(TeamId)
            && string.Equals(sessionId, SessionId, StringComparison.Ordinal))
        {
            var safeTeam = SanitizePathSegment(TeamId);
            return Path.Combine(sessionsPath, ParentSessionId, "teams", "_leaders", safeTeam);
        }

        if (Kind == AgentRunKind.TeamMember
            && ParentSessionId is not null
            && TeamTaskId is not null
            && string.Equals(sessionId, SessionId, StringComparison.Ordinal))
        {
            return Path.Combine(sessionsPath, ParentSessionId, "teams", TeamTaskId, "members", SessionId);
        }

        if (Kind == AgentRunKind.SubAgent
            && ParentSessionId is not null
            && string.Equals(sessionId, SessionId, StringComparison.Ordinal))
        {
            return Path.Combine(sessionsPath, ParentSessionId, "subagents", "default", SessionId);
        }

        return Path.Combine(sessionsPath, sessionId);
    }

    public static bool IsSubAgentSessionPath(string sessionJsonPath)
    {
        var parts = sessionJsonPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => string.Equals(part, "subagents", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsTeamMemberSessionPath(string sessionJsonPath)
    {
        var parts = sessionJsonPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!string.Equals(parts[i], "teams", StringComparison.OrdinalIgnoreCase)
                || i + 2 >= parts.Length)
            {
                continue;
            }

            if (string.Equals(parts[i + 2], "members", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(parts[i + 1], "_leaders", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value.Trim();
    }
}
