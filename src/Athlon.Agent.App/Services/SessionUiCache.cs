using System.Collections.Concurrent;
using System.Windows.Threading;

namespace Athlon.Agent.App.Services;

public sealed class SessionUiCache
{
    private const int MaxCachedSessions = 10;

    private readonly ConcurrentDictionary<string, SessionTurnUiController> _controllers = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly object _lruLock = new();
    private readonly Dispatcher _dispatcher;

    public SessionUiCache(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public SessionTurnUiController GetOrCreate(string sessionId, Action? requestScroll = null)
    {
        var controller = _controllers.GetOrAdd(sessionId, id => new SessionTurnUiController(_dispatcher, requestScroll));
        if (requestScroll is not null)
        {
            controller.RequestScroll = requestScroll;
        }

        Touch(sessionId);
        return controller;
    }

    public bool TryGet(string sessionId, out SessionTurnUiController? controller) =>
        _controllers.TryGetValue(sessionId, out controller);

    public void Remove(string sessionId)
    {
        _controllers.TryRemove(sessionId, out _);
        lock (_lruLock)
        {
            _lru.Remove(sessionId);
        }
    }

    private void Touch(string sessionId)
    {
        lock (_lruLock)
        {
            _lru.Remove(sessionId);
            _lru.AddFirst(sessionId);
            while (_lru.Count > MaxCachedSessions)
            {
                var evict = _lru.Last!.Value;
                _lru.RemoveLast();
                _controllers.TryRemove(evict, out _);
            }
        }
    }
}
