using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Caches session metadata and display logs for navigation between conversation history entries.
/// </summary>
public sealed class SessionNavigationStore(IFileStorageService storage)
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, AgentSession> _sessionCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ChatMessage>> _displayCache = new(StringComparer.Ordinal);

    public async Task<SessionNavigationSnapshot?> LoadSnapshotAsync(string sessionId)
    {
        var session = await LoadSessionAsync(sessionId).ConfigureAwait(true);
        if (session is null)
        {
            return null;
        }

        var displayMessages = await LoadDisplayMessagesAsync(sessionId).ConfigureAwait(true);
        return new SessionNavigationSnapshot(session, displayMessages);
    }

    private async Task<AgentSession?> LoadSessionAsync(string sessionId)
    {
        lock (_cacheLock)
        {
            if (_sessionCache.TryGetValue(sessionId, out var cached))
            {
                return cached;
            }
        }

        var loaded = await storage.LoadSessionAsync(sessionId).ConfigureAwait(true);
        if (loaded is not null)
        {
            lock (_cacheLock)
            {
                _sessionCache[sessionId] = loaded;
            }
        }

        return loaded;
    }

    private async Task<IReadOnlyList<ChatMessage>> LoadDisplayMessagesAsync(string sessionId)
    {
        lock (_cacheLock)
        {
            if (_displayCache.TryGetValue(sessionId, out var cached))
            {
                return cached;
            }
        }

        var messages = await storage.LoadConversationDisplayAsync(sessionId).ConfigureAwait(true);
        lock (_cacheLock)
        {
            _displayCache[sessionId] = messages;
        }

        return messages;
    }

    public async Task<AgentSession?> SaveIfNotEmptyAsync(AgentSession session)
    {
        if (session.Messages.Count == 0)
        {
            return null;
        }

        var toSave = SessionHistoryCoordinator.DeriveSessionTitle(session);
        await storage.SaveSessionAsync(toSave).ConfigureAwait(true);
        Invalidate(toSave.Id);
        return toSave;
    }

    public void Invalidate(string sessionId)
    {
        lock (_cacheLock)
        {
            _sessionCache.Remove(sessionId);
            _displayCache.Remove(sessionId);
        }
    }
}

public sealed record SessionNavigationSnapshot(
    AgentSession Session,
    IReadOnlyList<ChatMessage> DisplayMessages);
