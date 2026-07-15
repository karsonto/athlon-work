using System.Collections.Concurrent;
using System.Threading.Channels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.BehaviorReport;

public sealed class BehaviorEventManager : IEventManager, IDisposable
{
    private static readonly object InstanceLock = new();
    private static BehaviorEventManager? _instance;

    public static BehaviorEventManager Instance
    {
        get
        {
            if (_instance is not null)
            {
                return _instance;
            }

            lock (InstanceLock)
            {
                return _instance ??= new BehaviorEventManager();
            }
        }
    }

    private readonly ConcurrentQueue<BehaviorEvent> _preConfigureQueue = new();
    private readonly object _modelUsageLock = new();
    private readonly Dictionary<string, PurposeUsageBucket> _modelUsageBuckets = new(StringComparer.Ordinal);

    private AppSettings? _settings;
    private BehaviorEventLocalStore? _store;
    private BehaviorReportUploader? _uploader;
    private ClientDeviceInfo? _deviceInfo;
    private IAppLogger _logger = NullLogger.Instance;
    private Channel<BehaviorEvent>? _channel;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private Task? _timerTask;
    private int _configured;
    private int _started;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private BehaviorEventManager()
    {
    }

    /// <summary>True when configured and behavior reporting is enabled with a BaseUrl.</summary>
    public bool IsActive =>
        _configured == 1
        && _settings?.BehaviorReport is { Enabled: true } report
        && !string.IsNullOrWhiteSpace(report.BaseUrl);

    public void Configure(
        AppSettings settings,
        IAppPathProvider paths,
        HttpClient httpClient,
        IAppLogger logger,
        IImpSsoSessionStore? sessionStore = null,
        Func<string>? screenResolutionProvider = null,
        string? appName = null,
        string? appVersion = null)
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            // Allow refreshing device providers after App binds screen / version info.
            _settings = settings;
            _logger = logger.ForContext("BehaviorEventManager");
            _deviceInfo = new ClientDeviceInfo(sessionStore, screenResolutionProvider, appName, appVersion);
            if (_store is not null)
            {
                _uploader = new BehaviorReportUploader(httpClient, settings, _store, _deviceInfo, logger);
            }

            return;
        }

        _settings = settings;
        _logger = logger.ForContext("BehaviorEventManager");
        _store = new BehaviorEventLocalStore(paths);
        _deviceInfo = new ClientDeviceInfo(sessionStore, screenResolutionProvider, appName, appVersion);
        _uploader = new BehaviorReportUploader(httpClient, settings, _store, _deviceInfo, logger);
        _channel = Channel.CreateUnbounded<BehaviorEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        if (!settings.BehaviorReport.Enabled || string.IsNullOrWhiteSpace(settings.BehaviorReport.BaseUrl))
        {
            while (_preConfigureQueue.TryDequeue(out _))
            {
            }

            return;
        }

        while (_preConfigureQueue.TryDequeue(out var pending))
        {
            _channel.Writer.TryWrite(pending);
        }
    }

    public void Start()
    {
        try
        {
            if (_settings?.BehaviorReport is not { Enabled: true })
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.BehaviorReport.BaseUrl))
            {
                return;
            }

            if (_channel is null || _store is null || _uploader is null)
            {
                return;
            }

            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            _startedAt = DateTimeOffset.UtcNow;
            _deviceInfo?.GetSnapshot(forceRefresh: true);
            _workerCts = new CancellationTokenSource();
            var ct = _workerCts.Token;
            _workerTask = Task.Run(() => RunWriterLoopAsync(ct), ct);
            _timerTask = Task.Run(() => RunUploadLoopAsync(ct), ct);
        }
        catch (Exception ex)
        {
            _logger.Warning("BehaviorEventManager.Start failed: {Error}", ex.Message);
        }
    }

    public void Stop()
    {
        try
        {
            if (Interlocked.Exchange(ref _started, 0) == 0)
            {
                return;
            }

            _workerCts?.Cancel();
            try
            {
                Task.WaitAll(
                    new[] { _workerTask, _timerTask }.Where(t => t is not null).Cast<Task>().ToArray(),
                    TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore shutdown races
            }

            _workerCts?.Dispose();
            _workerCts = null;
            _workerTask = null;
            _timerTask = null;
        }
        catch (Exception ex)
        {
            _logger.Warning("BehaviorEventManager.Stop failed: {Error}", ex.Message);
        }
    }

    public void Record(
        string eventId,
        string eventType,
        string messageContent,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        try
        {
            // Configured + disabled → discard. Not yet configured → queue for SSO-before-DI.
            if (_configured == 1 && !IsActive)
            {
                return;
            }

            var evt = new BehaviorEvent
            {
                EventId = eventId,
                EventType = string.IsNullOrWhiteSpace(eventType) ? BehaviorEventTypes.Event : eventType,
                MessageContent = string.IsNullOrWhiteSpace(messageContent) ? eventId : messageContent,
                Parameters = parameters is null
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(parameters, StringComparer.Ordinal),
                Timestamp = DateTimeOffset.UtcNow
            };

            if (string.Equals(eventId, BehaviorEventIds.ModelCall, StringComparison.Ordinal))
            {
                TrackModelUsage(evt);
            }

            if (_channel is null)
            {
                // Cap pre-configure buffer for safety.
                if (_preConfigureQueue.Count < 500)
                {
                    _preConfigureQueue.Enqueue(evt);
                }

                return;
            }

            if (!IsActive)
            {
                return;
            }

            _channel.Writer.TryWrite(evt);
        }
        catch (Exception ex)
        {
            _logger.Warning("BehaviorEventManager.Record failed: {Error}", ex.Message);
        }
    }

    /// <summary>Records an attempt event when reporting is active; never throws.</summary>
    public void RecordAttempt(AgentAttemptEvent attempt)
    {
        try
        {
            if (!IsActive)
            {
                return;
            }

            var mapped = BehaviorAttemptEventMapper.Map(attempt);
            if (mapped is null)
            {
                return;
            }

            Record(mapped.Value.EventId, BehaviorEventTypes.Action, mapped.Value.EventId, mapped.Value.Parameters);
        }
        catch (Exception ex)
        {
            _logger.Warning("BehaviorEventManager.RecordAttempt failed: {Error}", ex.Message);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_settings?.BehaviorReport is not { Enabled: true } || _uploader is null || _store is null)
            {
                return;
            }

            // Drain in-memory queue first.
            if (_channel is not null)
            {
                while (_channel.Reader.TryRead(out var evt))
                {
                    await _store.AppendAsync(evt, cancellationToken).ConfigureAwait(false);
                }
            }

            EmitModelUsageSummaryIfAny();

            if (_channel is not null)
            {
                while (_channel.Reader.TryRead(out var evt))
                {
                    await _store.AppendAsync(evt, cancellationToken).ConfigureAwait(false);
                }
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(5));
            await _uploader.UploadPendingAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // timeout expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.Warning("BehaviorEventManager.FlushAsync failed: {Error}", ex.Message);
        }
    }

    public DateTimeOffset StartedAt => _startedAt;

    public void Dispose()
    {
        Stop();
    }

    /// <summary>Test helper to reset the process singleton between unit tests.</summary>
    internal static void ResetForTests()
    {
        lock (InstanceLock)
        {
            if (_instance is not null)
            {
                try
                {
                    _instance.Stop();
                }
                catch
                {
                    // ignore
                }

                _instance = null;
            }
        }
    }

    private async Task RunWriterLoopAsync(CancellationToken cancellationToken)
    {
        var channel = _channel;
        var store = _store;
        if (channel is null || store is null)
        {
            return;
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await store.AppendAsync(evt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning("Behavior event append failed: {Error}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
    }

    private async Task RunUploadLoopAsync(CancellationToken cancellationToken)
    {
        var minutes = Math.Max(1, _settings?.BehaviorReport.UploadIntervalMinutes ?? 10);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    EmitModelUsageSummaryIfAny();
                    // Allow writer to flush summary event.
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    if (_channel is not null && _store is not null)
                    {
                        while (_channel.Reader.TryRead(out var evt))
                        {
                            await _store.AppendAsync(evt, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (_uploader is not null)
                    {
                        await _uploader.UploadPendingAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning("Behavior upload cycle failed: {Error}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
    }

    private void TrackModelUsage(BehaviorEvent evt)
    {
        try
        {
            var purpose = evt.Parameters.TryGetValue("purpose", out var p) ? p?.ToString() ?? "Chat" : "Chat";
            var prompt = AsInt(evt.Parameters, "prompt_tokens");
            var completion = AsInt(evt.Parameters, "completion_tokens");
            lock (_modelUsageLock)
            {
                if (!_modelUsageBuckets.TryGetValue(purpose, out var bucket))
                {
                    bucket = new PurposeUsageBucket();
                    _modelUsageBuckets[purpose] = bucket;
                }

                bucket.Calls++;
                bucket.PromptTokens += prompt;
                bucket.CompletionTokens += completion;
            }
        }
        catch
        {
            // ignore analytics tracking errors
        }
    }

    private void EmitModelUsageSummaryIfAny()
    {
        Dictionary<string, object?>? parameters = null;
        lock (_modelUsageLock)
        {
            if (_modelUsageBuckets.Count == 0)
            {
                return;
            }

            parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["window_minutes"] = Math.Max(1, _settings?.BehaviorReport.UploadIntervalMinutes ?? 10)
            };

            foreach (var (purpose, bucket) in _modelUsageBuckets)
            {
                parameters[$"{purpose.ToLowerInvariant()}_calls"] = bucket.Calls;
                parameters[$"{purpose.ToLowerInvariant()}_prompt_tokens"] = bucket.PromptTokens;
                parameters[$"{purpose.ToLowerInvariant()}_completion_tokens"] = bucket.CompletionTokens;
            }

            _modelUsageBuckets.Clear();
        }

        if (parameters is null)
        {
            return;
        }

        // Bypass TrackModelUsage recursion — write directly.
        try
        {
            var evt = new BehaviorEvent
            {
                EventId = BehaviorEventIds.ModelUsageSummary,
                EventType = BehaviorEventTypes.Event,
                MessageContent = BehaviorEventIds.ModelUsageSummary,
                Parameters = parameters,
                Timestamp = DateTimeOffset.UtcNow
            };

            if (_channel is null)
            {
                _preConfigureQueue.Enqueue(evt);
            }
            else
            {
                _channel.Writer.TryWrite(evt);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Emit model_usage_summary failed: {Error}", ex.Message);
        }
    }

    private static int AsInt(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number
                && je.TryGetInt32(out var ji) => ji,
            _ => 0
        };
    }

    private sealed class PurposeUsageBucket
    {
        public int Calls;
        public int PromptTokens;
        public int CompletionTokens;
    }

    private sealed class NullLogger : IAppLogger
    {
        public static readonly NullLogger Instance = new();
        public IAppLogger ForContext(string sourceContext) => this;
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
    }
}
