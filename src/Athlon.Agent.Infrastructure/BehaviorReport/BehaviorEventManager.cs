using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Sso;
using System.Threading.Channels;

namespace Athlon.Agent.Infrastructure.BehaviorReport;

/// <summary>
/// Legacy compatibility shim.
/// The old behavior-reporting pipeline is retired; keep a no-op surface so existing call sites compile safely.
/// </summary>
public sealed class BehaviorEventManager : IEventManager, IDisposable
{
    private static readonly BehaviorEventManager Singleton = new();

    public static BehaviorEventManager Instance => Singleton;

    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;

    private readonly object _gate = new();
    private AppSettings _settings = new();
    private BehaviorEventLocalStore? _store;
    private BehaviorReportUploader? _uploader;
    private IAppLogger _logger = new NullLogger();
    private bool _started;
    private readonly Channel<BehaviorEvent> _channel = Channel.CreateUnbounded<BehaviorEvent>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    private BehaviorEventManager()
    {
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public static void ResetForTests()
    {
        Singleton.StartedAt = DateTimeOffset.UtcNow;
    }

    public BehaviorEventManager Configure(
        AppSettings settings,
        IAppPathProvider paths,
        HttpClient httpClient,
        IAppLogger logger,
        IImpSsoSessionStore? ssoStore = null,
        Func<string>? screenResolver = null,
        string productName = "athlon",
        string productVersion = "dev")
    {
        lock (_gate)
        {
            _settings = settings;
            _logger = logger.ForContext("BehaviorEventManager");
            _store = new BehaviorEventLocalStore(paths);
            _uploader = new BehaviorReportUploader(
                httpClient,
                settings,
                _store,
                new ClientDeviceInfo(
                    sessionStore: ssoStore,
                    screenResolutionProvider: screenResolver,
                    appName: productName,
                    appVersion: productVersion),
                _logger);
        }

        return this;
    }

    public void Record(
        string eventId,
        string eventType,
        string messageContent,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (!_started)
        {
            return;
        }

        // Keep a minimal, stable event envelope for upload.
        var evt = new BehaviorEvent
        {
            Timestamp = AppTimeZone.Now,
            EventId = eventId ?? string.Empty,
            EventType = string.IsNullOrWhiteSpace(eventType) ? BehaviorEventTypes.Event : eventType,
            MessageContent = string.IsNullOrWhiteSpace(messageContent) ? eventId ?? string.Empty : messageContent,
            Parameters = parameters is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
        };

        _channel.Writer.TryWrite(evt);
    }

    public void RecordAttempt(AgentAttemptEvent attempt)
    {
        if (!_started)
        {
            return;
        }

        var mapped = BehaviorAttemptEventMapper.Map(attempt);
        if (mapped is null)
        {
            return;
        }

        Record(
            mapped.Value.EventId,
            BehaviorEventTypes.Action,
            mapped.Value.EventId,
            mapped.Value.Parameters);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (_channel.Reader.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        BehaviorReportUploader? uploader;
        lock (_gate)
        {
            uploader = _uploader;
        }

        if (uploader is not null)
        {
            await uploader.UploadPendingAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Start()
    {
        StartedAt = DateTimeOffset.UtcNow;
        _started = true;
    }

    public void Stop()
    {
        _started = false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignore
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    AppSettings settings;
                    BehaviorEventLocalStore? store;
                    lock (_gate)
                    {
                        settings = _settings;
                        store = _store;
                    }

                    if (!settings.BehaviorReport.Enabled || store is null)
                    {
                        continue;
                    }

                    await store.AppendAsync(evt, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warning("BehaviorEventManager.Record append failed: {Error}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private sealed class NullLogger : IAppLogger
    {
        public IAppLogger ForContext(string sourceContext) => this;
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
    }
}

