using System.Windows.Threading;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// LRU cache of per-session <see cref="SessionTurnUiController"/> instances. Keeping a few
/// controllers alive means switching back to a recently viewed conversation reuses its rendered
/// messages and markdown view models instead of rebuilding them from scratch. Each controller
/// holds the session's messages plus its markdown cache, so the capacity is deliberately small.
/// </summary>
public sealed class SessionUiCache
{
    private const int DefaultCapacity = 4;

    private readonly Dictionary<string, SessionTurnUiController> _controllers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly ChatReplaySnapshotCache? _replayCache;
    private readonly int _capacity;

    public SessionUiCache(
        Dispatcher dispatcher,
        AppSettings settings,
        int capacity = DefaultCapacity,
        ChatReplaySnapshotCache? replayCache = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _dispatcher = dispatcher;
        _settings = settings;
        _capacity = capacity;
        _replayCache = replayCache;
    }

    public void AttachChatViewToAll(Controls.WebChatView? chatView)
    {
        if (chatView is null)
        {
            return;
        }

        foreach (var controller in SnapshotControllers())
        {
            controller.ChatView = chatView;
        }
    }

    public SessionTurnUiController GetOrCreate(
        string sessionId,
        Action? requestScroll = null,
        Action? requestScrollImmediate = null)
    {
        SessionTurnUiController controller;
        List<SessionTurnUiController>? evicted = null;
        lock (_gate)
        {
            if (!_controllers.TryGetValue(sessionId, out controller!))
            {
                controller = new SessionTurnUiController(_dispatcher, requestScroll, requestScrollImmediate)
                {
                    SessionId = sessionId,
                    ReplayCache = _replayCache,
                    PreserveSessionScroll = _settings.Ui.PreserveSessionUiState
                };
                _controllers[sessionId] = controller;
                _lru.AddFirst(sessionId);
                evicted = EvictOverflowLocked();
            }
            else
            {
                TouchLocked(sessionId);
            }
        }

        controller.SetShowToolCalls(true);
        // Always-on: migrate legacy false values when controllers are created.
        _settings.Ui.ShowToolCalls = true;
        if (requestScroll is not null)
        {
            controller.RequestScroll = requestScroll;
        }

        if (requestScrollImmediate is not null)
        {
            controller.RequestScrollImmediate = requestScrollImmediate;
        }

        ReleaseAll(evicted);
        return controller;
    }

    public void ApplyShowToolCalls(bool value = true)
    {
        foreach (var controller in SnapshotControllers())
        {
            controller.SetShowToolCalls(true);
        }
    }

    public bool TryGet(string sessionId, out SessionTurnUiController? controller)
    {
        lock (_gate)
        {
            if (_controllers.TryGetValue(sessionId, out controller))
            {
                TouchLocked(sessionId);
                return true;
            }
        }

        controller = null;
        return false;
    }

    public void Remove(string sessionId)
    {
        SessionTurnUiController? controller = null;
        lock (_gate)
        {
            _pinned.Remove(sessionId);
            if (_controllers.Remove(sessionId, out var found))
            {
                _lru.Remove(sessionId);
                controller = found;
            }
        }

        controller?.Release();
        _replayCache?.Remove(sessionId);
    }

    /// <summary>
    /// Pins a session's controller so LRU eviction skips it (used while a turn is running — the
    /// live turn holds buffered streaming state that must not be dropped). Unpin when the turn
    /// completes; a pinned session is otherwise still evictable by explicit <see cref="Remove"/>.
    /// </summary>
    public void SetPinned(string sessionId, bool pinned)
    {
        lock (_gate)
        {
            if (pinned)
            {
                _pinned.Add(sessionId);
            }
            else
            {
                _pinned.Remove(sessionId);
            }
        }
    }

    /// <summary>Touches <paramref name="sessionId"/>; must be called under <see cref="_gate"/>.</summary>
    private void TouchLocked(string sessionId)
    {
        _lru.Remove(sessionId);
        _lru.AddFirst(sessionId);
    }

    private List<SessionTurnUiController> EvictOverflowLocked()
    {
        List<SessionTurnUiController>? evicted = null;
        while (_controllers.Count > _capacity)
        {
            // Walk from the cold end and skip pinned (in-flight) sessions.
            var node = _lru.Last;
            while (node is not null && _pinned.Contains(node.Value))
            {
                node = node.Previous;
            }

            if (node is null)
            {
                // Everything left is pinned; evicting would break a running turn.
                break;
            }

            _lru.Remove(node);
            if (_controllers.Remove(node.Value, out var controller))
            {
                (evicted ??= []).Add(controller);
                _replayCache?.Remove(node.Value);
            }
        }

        return evicted ?? [];
    }

    private SessionTurnUiController[] SnapshotControllers()
    {
        lock (_gate)
        {
            return _controllers.Values.ToArray();
        }
    }

    private static void ReleaseAll(List<SessionTurnUiController>? controllers)
    {
        if (controllers is null)
        {
            return;
        }

        foreach (var controller in controllers)
        {
            controller.Release();
        }
    }
}
