using System.Collections.Concurrent;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed record SessionTurnRequest(
    string SessionId,
    AgentSession Session,
    string UserInput,
    IReadOnlyList<ImageAttachment> ImageAttachments,
    SessionTurnUiController Ui,
    bool IsAutoContinue = false);

public enum SessionTurnState
{
    Idle,
    Running
}

public sealed class SessionTurnCompletedEventArgs(
    string sessionId,
    AgentSession session,
    bool cancelled,
    bool timedOut,
    bool isAutoContinue,
    Exception? error)
{
    public string SessionId { get; } = sessionId;
    public AgentSession Session { get; } = session;
    public bool Cancelled { get; } = cancelled;
    public bool TimedOut { get; } = timedOut;
    public bool IsAutoContinue { get; } = isAutoContinue;
    public Exception? Error { get; } = error;
}

public sealed class SessionTurnHost
{
    public const int MaxConcurrentTurns = 3;

    private readonly IAgentOrchestrator _orchestrator;
    private readonly IFileStorageService _storage;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, SessionTurnRunner> _runners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<QueuedTurnPayload>> _queues = new(StringComparer.Ordinal);
    private readonly object _startGate = new();

    public SessionTurnHost(IAgentOrchestrator orchestrator, IFileStorageService storage, AppSettings settings)
    {
        _orchestrator = orchestrator;
        _storage = storage;
        _settings = settings;
    }

    public event EventHandler<SessionTurnCompletedEventArgs>? TurnCompleted;
    public event EventHandler<string>? TurnStateChanged;

    public bool TryStart(SessionTurnRequest request, out string? error)
    {
        error = null;
        lock (_startGate)
        {
            if (_runners.ContainsKey(request.SessionId))
            {
                error = "当前对话正在生成，请等待完成或先停止。";
                return false;
            }

            if (_runners.Count >= MaxConcurrentTurns)
            {
                error = "已有 3 个对话在生成，请等待或停止其中一个。";
                return false;
            }

            var timeout = _settings.AgentTurn.ResolveTurnTimeout();
            var timeoutMinutes = _settings.AgentTurn.ResolveTurnTimeoutMinutes();
            var runner = new SessionTurnRunner(this, request, timeout, timeoutMinutes);
            _runners[request.SessionId] = runner;
            TurnStateChanged?.Invoke(this, request.SessionId);
            runner.Start();
            return true;
        }
    }

    public bool IsRunning(string sessionId) => _runners.ContainsKey(sessionId);

    public IReadOnlyCollection<string> RunningSessionIds => _runners.Keys.ToArray();

    public void Enqueue(QueuedTurnPayload payload)
    {
        lock (_startGate)
        {
            var queue = _queues.GetOrAdd(payload.SessionId, _ => new Queue<QueuedTurnPayload>());
            queue.Enqueue(payload);
        }
    }

    public bool TryDequeue(string sessionId, out QueuedTurnPayload? payload)
    {
        lock (_startGate)
        {
            if (!_queues.TryGetValue(sessionId, out var queue) || queue.Count == 0)
            {
                payload = null;
                return false;
            }

            payload = queue.Dequeue();
            if (queue.Count == 0)
            {
                _queues.TryRemove(sessionId, out _);
            }

            return true;
        }
    }

    public void RequeueFront(QueuedTurnPayload payload)
    {
        lock (_startGate)
        {
            var queue = _queues.GetOrAdd(payload.SessionId, _ => new Queue<QueuedTurnPayload>());
            var items = queue.ToList();
            queue.Clear();
            queue.Enqueue(payload);
            foreach (var item in items)
            {
                queue.Enqueue(item);
            }
        }
    }

    public bool Remove(string sessionId, string queueId)
    {
        lock (_startGate)
        {
            if (!_queues.TryGetValue(sessionId, out var queue) || queue.Count == 0)
            {
                return false;
            }

            var items = queue.Where(item => !string.Equals(item.QueueId, queueId, StringComparison.Ordinal)).ToList();
            if (items.Count == queue.Count)
            {
                return false;
            }

            queue.Clear();
            foreach (var item in items)
            {
                queue.Enqueue(item);
            }

            if (queue.Count == 0)
            {
                _queues.TryRemove(sessionId, out _);
            }

            return true;
        }
    }

    public void ClearQueue(string sessionId)
    {
        lock (_startGate)
        {
            _queues.TryRemove(sessionId, out _);
        }
    }

    public int GetQueueCount(string sessionId)
    {
        lock (_startGate)
        {
            return _queues.TryGetValue(sessionId, out var queue) ? queue.Count : 0;
        }
    }

    public bool HasQueuedTurns(string sessionId) => GetQueueCount(sessionId) > 0;

    public void Cancel(string sessionId)
    {
        if (_runners.TryGetValue(sessionId, out var runner))
        {
            runner.Cancel();
        }
    }

    public void CancelAll()
    {
        foreach (var runner in _runners.Values)
        {
            runner.Cancel();
        }
    }

    private void OnRunnerFinished(
        SessionTurnRunner runner,
        AgentSession session,
        bool cancelled,
        bool timedOut,
        Exception? error)
    {
        _runners.TryRemove(runner.SessionId, out _);
        TurnStateChanged?.Invoke(this, runner.SessionId);
        TurnCompleted?.Invoke(
            this,
            new SessionTurnCompletedEventArgs(
                runner.SessionId,
                session,
                cancelled,
                timedOut,
                runner.IsAutoContinue,
                error));
    }

    private sealed class SessionTurnRunner
    {
        private readonly SessionTurnHost _host;
        private readonly SessionTurnRequest _request;
        private readonly TimeSpan _timeout;
        private readonly int _timeoutMinutes;
        private CancellationTokenSource? _cancellation;
        private CancellationTokenSource? _timeoutCancellation;
        private CancellationTokenSource? _linked;
        private AgentSession _session;

        public SessionTurnRunner(SessionTurnHost host, SessionTurnRequest request, TimeSpan timeout, int timeoutMinutes)
        {
            _host = host;
            _request = request;
            _timeout = timeout;
            _timeoutMinutes = timeoutMinutes;
            _session = request.Session;
        }

        public string SessionId => _request.SessionId;

        public bool IsAutoContinue => _request.IsAutoContinue;

        public void Start()
        {
            _cancellation = new CancellationTokenSource();
            _timeoutCancellation = new CancellationTokenSource(_timeout);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, _timeoutCancellation.Token);
            _ = RunAsync();
        }

        public void Cancel() => _cancellation?.Cancel();

        private async Task RunAsync()
        {
            var cancelled = false;
            Exception? error = null;
            var timedOut = false;

            try
            {
                _request.Ui.ResetForTurn();
                var callbacks = _request.Ui.BuildCallbacks();
                _session = await _host._orchestrator.SendAsync(
                    _session,
                    _request.UserInput,
                    _request.ImageAttachments,
                    callbacks,
                    _linked!.Token).ConfigureAwait(false);
                await _host._storage.SaveSessionAsync(_session).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                timedOut = _timeoutCancellation is { IsCancellationRequested: true }
                           && _cancellation is { IsCancellationRequested: false };
                var reloaded = await _host._storage.LoadSessionAsync(SessionId).ConfigureAwait(false);
                if (reloaded is not null)
                {
                    _session = reloaded;
                }

                await _host._storage.SaveSessionAsync(_session).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
                await _host._storage.SaveSessionAsync(_session).ConfigureAwait(false);
            }
            finally
            {
                _request.Ui.FinalizeTurn(
                    _session,
                    cancelled,
                    timedOut,
                    _timeoutMinutes,
                    error is null ? null : $"模型调用失败：{error.Message}");
                _linked?.Dispose();
                _timeoutCancellation?.Dispose();
                _cancellation?.Dispose();
                _host.OnRunnerFinished(this, _session, cancelled, timedOut, error);
            }
        }
    }
}
