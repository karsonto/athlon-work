namespace Athlon.Agent.Core.SubAgents;

/// <summary>
/// Redirects session file I/O for a sub-agent run to
/// {sessions}/{parentId}/subagents/default/{subSessionId}/.
/// </summary>
[Obsolete("Use IAgentRunContextAccessor.Push with AgentRunContext instead.")]
public sealed class AmbientSubAgentStorageScope : IDisposable
{
    private static readonly AsyncLocal<SubAgentStorageContext?> Ambient = new();

    private readonly SubAgentStorageContext? _previous;

    private AmbientSubAgentStorageScope(SubAgentStorageContext context)
    {
        _previous = Ambient.Value;
        Ambient.Value = context;
    }

    public static SubAgentStorageContext? Current => Ambient.Value;

    public static IDisposable Enter(string parentSessionId, string subSessionId) =>
        new AmbientSubAgentStorageScope(new SubAgentStorageContext(parentSessionId, subSessionId));

    public static bool IsSubAgentSessionPath(string sessionJsonPath)
    {
        var parts = sessionJsonPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => string.Equals(part, "subagents", StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveSessionDirectory(string sessionsPath, string sessionId)
    {
        var context = Ambient.Value;
        if (context is not null
            && string.Equals(sessionId, context.SubSessionId, StringComparison.Ordinal))
        {
            return Path.Combine(
                sessionsPath,
                context.ParentSessionId,
                "subagents",
                "default",
                context.SubSessionId);
        }

        return Path.Combine(sessionsPath, sessionId);
    }

    public void Dispose() => Ambient.Value = _previous;

    public sealed record SubAgentStorageContext(string ParentSessionId, string SubSessionId);
}
