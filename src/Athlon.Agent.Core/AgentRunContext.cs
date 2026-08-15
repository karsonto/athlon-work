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
            RunId = Guid.NewGuid().ToString("N"),
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

    public string ResolveSessionDirectory(string sessionsPath, string sessionId)
    {
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
}
