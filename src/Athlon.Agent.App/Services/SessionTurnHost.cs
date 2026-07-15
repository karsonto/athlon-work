using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Infrastructure.BehaviorReport;

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
            RecordTurnLifecycle(request, "started");
            if (!request.IsAutoContinue)
            {
                RecordUserMessageSent(request);
            }

            runner.Start();
            return true;
        }
    }

    private static void RecordUserMessageSent(SessionTurnRequest request)
    {
        try
        {
            EventManager.Instance.Record(
                BehaviorEventIds.UserMessageSent,
                BehaviorEventTypes.Action,
                BehaviorEventIds.UserMessageSent,
                new Dictionary<string, object?>
                {
                    ["session_id"] = request.SessionId,
                    ["has_image"] = request.ImageAttachments.Count > 0,
                    ["message_length"] = request.UserInput?.Length ?? 0
                });
        }
        catch
        {
            // ignore
        }
    }

    private static void RecordTurnLifecycle(SessionTurnRequest request, string outcome, Exception? error = null)
    {
        try
        {
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["session_id"] = request.SessionId,
                ["run_id"] = request.SessionId,
                ["outcome"] = outcome
            };
            if (error is not null)
            {
                parameters["error_type"] = error.GetType().Name;
            }

            EventManager.Instance.Record(
                BehaviorEventIds.Turn,
                BehaviorEventTypes.Event,
                BehaviorEventIds.Turn,
                parameters);
        }
        catch
        {
            // ignore
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

    public bool HasActiveWork
    {
        get
        {
            if (!_runners.IsEmpty)
            {
                return true;
            }

            lock (_startGate)
            {
                return _queues.Values.Any(queue => queue.Count > 0);
            }
        }
    }

    public void ClearAllQueues()
    {
        lock (_startGate)
        {
            _queues.Clear();
        }
    }

    public async Task ShutdownAsync(TimeSpan waitTimeout, CancellationToken cancellationToken = default)
    {
        CancelAll();
        ClearAllQueues();

        var tasks = _runners.Values.Select(runner => runner.Completion).ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        await Task
            .WhenAny(Task.WhenAll(tasks), Task.Delay(waitTimeout, cancellationToken))
            .ConfigureAwait(false);
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

        var outcome = error is not null
            ? "failed"
            : cancelled || timedOut
                ? "cancelled"
                : "completed";
        try
        {
            EventManager.Instance.Record(
                BehaviorEventIds.Turn,
                BehaviorEventTypes.Event,
                BehaviorEventIds.Turn,
                new Dictionary<string, object?>
                {
                    ["session_id"] = runner.SessionId,
                    ["run_id"] = runner.SessionId,
                    ["outcome"] = outcome,
                    ["timed_out"] = timedOut,
                    ["error_type"] = error?.GetType().Name
                });
        }
        catch
        {
            // ignore
        }

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
        private readonly TimeSpan? _timeout;
        private readonly int _timeoutMinutes;
        private CancellationTokenSource? _cancellation;
        private CancellationTokenSource? _timeoutCancellation;
        private CancellationTokenSource? _linked;
        private AgentSession _session;
        private Task? _runTask;

        public SessionTurnRunner(SessionTurnHost host, SessionTurnRequest request, TimeSpan? timeout, int timeoutMinutes)
        {
            _host = host;
            _request = request;
            _timeout = timeout;
            _timeoutMinutes = timeoutMinutes;
            _session = request.Session;
        }

        public string SessionId => _request.SessionId;

        public bool IsAutoContinue => _request.IsAutoContinue;

        public Task Completion => _runTask ?? Task.CompletedTask;

        public void Start()
        {
            _cancellation = new CancellationTokenSource();
            if (_timeout is { } timeout)
            {
                _timeoutCancellation = new CancellationTokenSource(timeout);
                _linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, _timeoutCancellation.Token);
            }

            _runTask = RunAsync();
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
                var liveSession = new LiveAgentSession(_session);
                var eventBridge = new AgentRunEventBridge();
                var callbacks = eventBridge.BuildCallbacks(_request.Ui, liveSession);
                var turnToken = _linked?.Token ?? _cancellation!.Token;
                _session = await _host._orchestrator.SendAsync(
                    _session,
                    _request.UserInput,
                    _request.ImageAttachments,
                    callbacks,
                    turnToken).ConfigureAwait(false);
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
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                var errorMessage = error is null ? null : TurnFailureMessages.FormatModelCallFailure(error);
                IReadOnlyList<ChatMessage> persistedTurnMessages = Array.Empty<ChatMessage>();
                if (cancelled || timedOut || error is not null)
                {
                    var snapshot = _request.Ui.CaptureEndSnapshot(_session, cancelled, timedOut, errorMessage);
                    var reconcileResult = SessionTurnReconciler.Reconcile(_session, snapshot);
                    _session = reconcileResult.Session;
                    persistedTurnMessages = reconcileResult.PersistedMessages;
                    foreach (var message in persistedTurnMessages)
                    {
                        await _host._storage.AppendConversationMessageAsync(_session.Id, message).ConfigureAwait(false);
                    }
                }

                _session = SessionHistoryCoordinator.DeriveSessionTitle(_session);
                await _host._storage.SaveSessionAsync(_session).ConfigureAwait(false);
                _request.Ui.FinalizeTurn(
                    _session,
                    persistedTurnMessages,
                    cancelled,
                    timedOut,
                    _timeoutMinutes,
                    errorMessage);
                _linked?.Dispose();
                _timeoutCancellation?.Dispose();
                _cancellation?.Dispose();
                _host.OnRunnerFinished(this, _session, cancelled, timedOut, error);
            }
        }
    }
}
