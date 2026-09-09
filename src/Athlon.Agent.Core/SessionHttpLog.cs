namespace Athlon.Agent.Core;

public interface IActiveAgentSessionContext
{
    string? SessionId { get; }

    void SetSession(string? sessionId);

    IDisposable Enter(string sessionId);
}

public interface ISessionHttpLogService
{
    /// <summary>Whether HTTP interaction logging is active. Callers may short-circuit to avoid building/serializing logs.</summary>
    bool IsEnabled { get; }

    Task LogInteractionAsync(string? sessionId, SessionHttpInteractionLog entry, CancellationToken cancellationToken = default);
}

public sealed record SessionHttpInteractionLog(
    DateTimeOffset Timestamp,
    string Endpoint,
    string Purpose,
    int? StatusCode,
    object? Request,
    string? ResponseBody,
    string? Error,
    long DurationMs,
    string? RequestId = null);
