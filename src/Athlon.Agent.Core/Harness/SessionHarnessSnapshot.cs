namespace Athlon.Agent.Core.Harness;

public sealed class SessionHarnessFile
{
    public bool Enabled { get; set; }
}

public sealed record SessionHarnessSnapshot(bool Enabled)
{
    public static SessionHarnessSnapshot Empty { get; } = new(false);
}

public interface ISessionHarnessState
{
    Task LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default);

    SessionHarnessSnapshot GetSnapshot(string? sessionId);

    bool IsEnabled(string? sessionId);

    bool IsEnabledForActiveRun(IAgentRunContextAccessor runContextAccessor);
}
