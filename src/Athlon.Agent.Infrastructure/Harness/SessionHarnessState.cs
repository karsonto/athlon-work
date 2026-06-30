using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class SessionHarnessState(
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor) : ISessionHarnessState
{
    private static readonly SessionHarnessSnapshot EmptySnapshot = SessionHarnessSnapshot.Empty;
    private readonly ConcurrentDictionary<string, SessionHarnessSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var path = GetHarnessFilePath(sessionId);
        var file = await jsonFileStore.LoadAsync<SessionHarnessFile>(path, cancellationToken).ConfigureAwait(false);
        _cache[sessionId] = SessionHarnessSnapshot.FromPersisted(file);
    }

    public async Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var path = GetHarnessFilePath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await jsonFileStore.SaveAsync(
            path,
            new SessionHarnessFile { Mode = state.ToPersistedMode() },
            cancellationToken).ConfigureAwait(false);
        _cache[sessionId] = state;
    }

    public SessionHarnessSnapshot GetSnapshot(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return EmptySnapshot;
        }

        return _cache.TryGetValue(sessionId, out var snapshot) ? snapshot : EmptySnapshot;
    }

    public SessionAgentMode GetMode(string? sessionId) => GetSnapshot(sessionId).Mode;

    public bool IsCodingMode(string? sessionId) => GetMode(sessionId) == SessionAgentMode.Coding;

    public bool IsAskMode(string? sessionId) => GetMode(sessionId) == SessionAgentMode.Ask;

    public bool IsEnabled(string? sessionId) => IsCodingMode(sessionId);

    public bool IsCodingModeForActiveRun(IAgentRunContextAccessor accessor) =>
        IsActiveRunMode(accessor, IsCodingMode);

    public bool IsAskModeForActiveRun(IAgentRunContextAccessor accessor) =>
        IsActiveRunMode(accessor, IsAskMode);

    public bool IsEnabledForActiveRun(IAgentRunContextAccessor accessor) =>
        IsCodingModeForActiveRun(accessor);

    private bool IsActiveRunMode(IAgentRunContextAccessor accessor, Func<string?, bool> predicate)
    {
        var run = accessor.Current;
        if (run is null || run.Kind == AgentRunKind.SubAgent)
        {
            return false;
        }

        return predicate(run.SessionId);
    }

    private string GetHarnessFilePath(string sessionId)
    {
        var sessionDir = runContextAccessor.ResolveSessionDirectory(paths.SessionsPath, sessionId);
        return Path.Combine(sessionDir, "harness.json");
    }
}
