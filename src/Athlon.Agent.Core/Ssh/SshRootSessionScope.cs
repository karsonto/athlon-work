namespace Athlon.Agent.Core;

/// <summary>
/// Per-async-flow root session id used for routing SSH connections.
/// First-entered wins: a root session turn enters its own id, while nested
/// sub-agent turns (which carry a sub-session id) inherit the existing root
/// instead of replacing it, so the whole conversation tree shares one SSH slot.
/// Distinct concurrent root turns run in separate async flows and never cross-talk.
/// </summary>
public sealed class SshRootSessionScope : IDisposable
{
    private static readonly AsyncLocal<string?> Current = new();

    private readonly string? _previous;

    private SshRootSessionScope(string? sessionId)
    {
        _previous = Current.Value;
        Current.Value = sessionId;
    }

    /// <summary>The root session id in scope for the current async flow, if any.</summary>
    public static string? CurrentSessionId => Current.Value;

    /// <summary>
    /// Enters <paramref name="sessionId"/> only when no root scope is already present.
    /// Nested flows (sub-agents) inherit the outer root scope and get a no-op scope back.
    /// </summary>
    public static IDisposable EnterIfAbsent(string? sessionId)
    {
        var existing = Current.Value;
        if (!string.IsNullOrEmpty(existing))
        {
            // Already inside a root turn's async flow (e.g. a sub-agent turn).
            return NullScope.Instance;
        }

        return new SshRootSessionScope(
            string.IsNullOrWhiteSpace(sessionId) ? null : sessionId);
    }

    public void Dispose() => Current.Value = _previous;

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
            // No-op: nothing was entered.
        }
    }
}
