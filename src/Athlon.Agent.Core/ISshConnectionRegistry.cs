namespace Athlon.Agent.Core;

/// <summary>
/// Manages independent SSH connections keyed by root session id. Every root session
/// keeps its own connection slot so switching the displayed session (or a background
/// turn running in another session) never tears down an in-use connection. Slots that
/// stay idle for the configured span are reclaimed automatically.
/// </summary>
public interface ISshConnectionRegistry
{
    /// <summary>
    /// Root session id that non-turn (UI) callers resolve to. Mirrors the currently
    /// displayed session; updated whenever the shell switches sessions.
    /// </summary>
    string? DefaultSessionId { get; set; }

    /// <summary>True when the given root session currently holds a live SSH connection.</summary>
    bool IsSessionConnected(string rootSessionId);

    /// <summary>
    /// Ensures the given root session holds a connection matching <paramref name="request"/>.
    /// Reuses an existing matching slot; reconnects only when the target changed.
    /// </summary>
    Task EnsureConnectedAsync(
        string rootSessionId,
        SshConnectRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Drops the connection slot for a single root session.</summary>
    Task DisconnectAsync(string rootSessionId, CancellationToken cancellationToken = default);

    /// <summary>Drops every connection slot (used on shutdown).</summary>
    Task DisconnectAllAsync(CancellationToken cancellationToken = default);
}
