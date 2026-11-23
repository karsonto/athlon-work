using System.IO;
using System.Text.Json.Serialization;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.Room;

/// <summary>
/// Persists the per-room @agent <see cref="AgentSession"/> under <c>rooms/{roomId}/agent-session.json</c>
/// so successive mentions continue the same transcript and get normal context compaction.
/// Also tracks which room chat message ids have been mirrored into that session.
/// </summary>
public sealed class RoomAgentSessionStore
{
    private readonly string _roomsRoot;
    private readonly IJsonFileStore _json;

    public RoomAgentSessionStore(IAppPathProvider paths, IJsonFileStore json)
    {
        _roomsRoot = Path.Combine(paths.RootPath, "rooms");
        Directory.CreateDirectory(_roomsRoot);
        _json = json;
    }

    public static string StableSessionId(string roomId) => "room-agent-" + roomId;

    public static string RoomChatMessageId(string roomMessageId) => "room:" + roomMessageId;

    public string GetSessionPath(string roomId)
    {
        if (!RoomStore.IsSafeId(roomId))
        {
            throw new ArgumentException("Invalid room id.", nameof(roomId));
        }

        return Path.Combine(_roomsRoot, roomId, "agent-session.json");
    }

    public string GetSyncPath(string roomId)
    {
        if (!RoomStore.IsSafeId(roomId))
        {
            throw new ArgumentException("Invalid room id.", nameof(roomId));
        }

        return Path.Combine(_roomsRoot, roomId, "agent-room-sync.json");
    }

    public async Task<AgentSession> LoadOrCreateAsync(
        string roomId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var path = GetSessionPath(roomId);
        var stableId = StableSessionId(roomId);
        try
        {
            var existing = await _json.LoadAsync<AgentSession>(path, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.Id, stableId, StringComparison.Ordinal))
                {
                    existing = existing with { Id = stableId };
                }

                if (string.IsNullOrWhiteSpace(existing.Title))
                {
                    existing = existing.WithTitle(title);
                }

                return existing;
            }
        }
        catch
        {
            // Corrupt / unreadable → start fresh below.
        }

        return AgentSession.Create(title) with { Id = stableId };
    }

    public async Task SaveAsync(
        string roomId,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        var path = GetSessionPath(roomId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _json.SaveAsync(path, session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomAgentSyncState> LoadSyncStateAsync(
        string roomId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await _json
                .LoadAsync<RoomAgentSyncState>(GetSyncPath(roomId), cancellationToken)
                .ConfigureAwait(false);
            if (loaded?.SyncedMessageIds is { Count: > 0 })
            {
                return loaded;
            }
        }
        catch
        {
            // fall through
        }

        return new RoomAgentSyncState();
    }

    public async Task SaveSyncStateAsync(
        string roomId,
        RoomAgentSyncState state,
        CancellationToken cancellationToken = default)
    {
        var path = GetSyncPath(roomId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _json.SaveAsync(path, state, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RoomAgentSyncState
{
    [JsonPropertyName("syncedMessageIds")]
    public List<string> SyncedMessageIds { get; set; } = new();
}
