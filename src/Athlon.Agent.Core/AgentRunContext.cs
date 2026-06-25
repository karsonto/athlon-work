using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Core;

public sealed record AgentRunContext
{
    public required string SessionId { get; init; }

    public required string RunId { get; init; }

    public string? ParentSessionId { get; init; }

    public string? WorkspaceRoot { get; init; }

    public IReadOnlyList<string> WorkspaceIgnorePatterns { get; init; } = WorkspaceIgnoreDefaults.BuiltIn;

    public required IToolRouter ToolRouter { get; init; }

    public required ISystemPromptOrchestrator PromptOrchestrator { get; init; }

    public string? SubAgentRole { get; init; }

    public AgentLoopOptions? LoopOptions { get; init; }

    /// <summary>When set, overrides <see cref="ToolPermissionSettings.RequireToolApproval"/> for this run.</summary>
    public bool? RequireToolApproval { get; init; }

    public AgentRunKind Kind { get; init; } = AgentRunKind.Root;

    public static AgentRunContext CreateRoot(
        AgentSession session,
        string runId,
        IToolRouter toolRouter,
        ISystemPromptOrchestrator promptOrchestrator,
        IReadOnlyList<string> ignorePatterns) =>
        new()
        {
            SessionId = session.Id,
            RunId = runId,
            WorkspaceRoot = string.IsNullOrWhiteSpace(session.ActiveWorkspace)
                ? null
                : Path.GetFullPath(session.ActiveWorkspace),
            WorkspaceIgnorePatterns = ignorePatterns,
            ToolRouter = toolRouter,
            PromptOrchestrator = promptOrchestrator,
            Kind = AgentRunKind.Root
        };

    public AgentRunContext CreateChild(
        string subSessionId,
        IToolRouter childRouter,
        ISystemPromptOrchestrator childPrompt,
        string role,
        AgentLoopOptions? loopOptions,
        string? workspaceRoot,
        IReadOnlyList<string> ignorePatterns) =>
        new()
        {
            SessionId = subSessionId,
            RunId = Guid.NewGuid().ToString("N"),
            ParentSessionId = Kind == AgentRunKind.SubAgent && ParentSessionId is not null
                ? ParentSessionId
                : SessionId,
            WorkspaceRoot = workspaceRoot,
            WorkspaceIgnorePatterns = ignorePatterns,
            ToolRouter = childRouter,
            PromptOrchestrator = childPrompt,
            SubAgentRole = role,
            LoopOptions = loopOptions,
            Kind = AgentRunKind.SubAgent
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
