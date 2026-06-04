namespace Athlon.Agent.Core.SubAgents;

/// <summary>
/// Redirects session file I/O for a sub-agent run to
/// {sessions}/{parentId}/subagents/default/{subSessionId}/.
/// </summary>
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
