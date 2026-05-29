namespace Athlon.Agent.Core;

public interface IActiveAgentSessionContext
{
    string? SessionId { get; }

    void SetSession(string? sessionId);

    IDisposable Enter(string sessionId);
}

public interface ISessionHttpLogService
{
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
    long DurationMs);
