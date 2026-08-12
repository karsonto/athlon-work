using System.Collections.Concurrent;
using EasyWindowsTerminalControl;

namespace Athlon.Agent.App.Services.Terminal;

public sealed record TerminalSessionEntry(TermPTY Session, TerminalOutputBuffer Buffer);

/// <summary>Maps Terminal workspace tab ids to live ConPTY sessions.</summary>
public sealed class TerminalSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, TerminalSessionEntry> _sessions = new();

    public void Register(Guid tabId, TermPTY session, TerminalOutputBuffer buffer) =>
        _sessions[tabId] = new TerminalSessionEntry(session, buffer);

    public void Unregister(Guid tabId) =>
        _sessions.TryRemove(tabId, out _);

    public bool TryGet(Guid tabId, out TerminalSessionEntry? entry) =>
        _sessions.TryGetValue(tabId, out entry);

    public IReadOnlyCollection<Guid> RegisteredTabIds => _sessions.Keys.ToArray();
}
