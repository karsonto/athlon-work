namespace Athlon.Agent.Core.Harness;

public sealed class DefaultSessionHarnessState : ISessionHarnessState
{
    public static DefaultSessionHarnessState Instance { get; } = new();

    public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public SessionHarnessSnapshot GetSnapshot(string? sessionId) => SessionHarnessSnapshot.Empty;

    public SessionAgentMode GetMode(string? sessionId) => SessionAgentMode.Agent;

    public bool IsCodingMode(string? sessionId) => false;

    public bool IsAskMode(string? sessionId) => false;

    public bool IsEnabled(string? sessionId) => false;

    public bool IsCodingModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => false;

    public bool IsAskModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => false;

    public bool IsEnabledForActiveRun(IAgentRunContextAccessor runContextAccessor) => false;
}
