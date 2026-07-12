using System.Collections.Concurrent;
using System.Windows.Threading;
using Athlon.Agent.App.Localization;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class SessionUiCache
{
    private const int MaxCachedSessions = 8;

    private readonly ConcurrentDictionary<string, SessionTurnUiController> _controllers = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly object _lruLock = new();
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly IUserNotifier? _notifier;

    public SessionUiCache(Dispatcher dispatcher, AppSettings settings, IUserNotifier? notifier = null)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _notifier = notifier;
    }

    public void AttachChatViewToAll(Controls.WebChatView? chatView)
    {
        if (chatView is null)
        {
            return;
        }

        foreach (var controller in _controllers.Values)
        {
            controller.ChatView = chatView;
        }
    }

    public SessionTurnUiController GetOrCreate(
        string sessionId,
        Action? requestScroll = null,
        Action? requestScrollImmediate = null)
    {
        var controller = _controllers.GetOrAdd(
            sessionId,
            id => new SessionTurnUiController(_dispatcher, requestScroll, requestScrollImmediate, _notifier));
        controller.SetShowToolCalls(_settings.Ui.ShowToolCalls);
        if (requestScroll is not null)
        {
            controller.RequestScroll = requestScroll;
        }

        if (requestScrollImmediate is not null)
        {
            controller.RequestScrollImmediate = requestScrollImmediate;
        }

        Touch(sessionId);
        return controller;
    }

    public void ApplyShowToolCalls(bool value)
    {
        foreach (var controller in _controllers.Values)
        {
            controller.SetShowToolCalls(value);
        }
    }

    public bool TryGet(string sessionId, out SessionTurnUiController? controller) =>
        _controllers.TryGetValue(sessionId, out controller);

    public void Remove(string sessionId)
    {
        if (_controllers.TryRemove(sessionId, out var controller))
        {
            controller.Release();
        }

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
                if (_controllers.TryRemove(evict, out var controller))
                {
                    controller.Release();
                }
            }
        }
    }
}
