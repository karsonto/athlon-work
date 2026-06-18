using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class SessionKnowledgeState(IAppPathProvider paths, IJsonFileStore jsonFileStore) : ISessionKnowledgeState
{
    private static readonly SessionKnowledgeSnapshot EmptySnapshot = SessionKnowledgeSnapshot.Empty;
    private readonly ConcurrentDictionary<string, SessionKnowledgeSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var path = GetKnowledgeFilePath(sessionId);
        var file = await jsonFileStore.LoadAsync<SessionKnowledgeFile>(path, cancellationToken).ConfigureAwait(false);
        _cache[sessionId] = file is null ? EmptySnapshot : ToSnapshot(file);
    }

    public async Task SaveAsync(string sessionId, SessionKnowledgeSnapshot state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var path = GetKnowledgeFilePath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new SessionKnowledgeFile
        {
            Enabled = state.Enabled,
            ModuleIds = state.ModuleIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList()
        };
        await jsonFileStore.SaveAsync(path, file, cancellationToken).ConfigureAwait(false);
        _cache[sessionId] = ToSnapshot(file);
    }

    public SessionKnowledgeSnapshot GetSnapshot(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return EmptySnapshot;
        }

        return _cache.TryGetValue(sessionId, out var snapshot) ? snapshot : EmptySnapshot;
    }

    public bool ShouldExposeKnowledgeTool(string? sessionId)
    {
        var snapshot = GetSnapshot(sessionId);
        return snapshot.Enabled && snapshot.ModuleIds.Count > 0;
    }

    public async Task<IReadOnlySet<string>> GetModuleIdsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return EmptySnapshot.ModuleIds;
        }

        if (!_cache.ContainsKey(sessionId))
        {
            await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = GetSnapshot(sessionId);
        return snapshot.Enabled ? snapshot.ModuleIds : EmptySnapshot.ModuleIds;
    }

    private string GetKnowledgeFilePath(string sessionId)
    {
        var sessionDir = AmbientSubAgentStorageScope.ResolveSessionDirectory(paths.SessionsPath, sessionId);
        return Path.Combine(sessionDir, "knowledge.json");
    }

    private static SessionKnowledgeSnapshot ToSnapshot(SessionKnowledgeFile file)
    {
        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var moduleId in file.ModuleIds)
        {
            if (!string.IsNullOrWhiteSpace(moduleId))
            {
                moduleIds.Add(moduleId);
            }
        }

        return new SessionKnowledgeSnapshot(file.Enabled, moduleIds);
    }
}
