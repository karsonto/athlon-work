using System.IO;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Infrastructure.RuntimeDiagnostics;

public sealed class RuntimeDiagnosticEventSink : IRuntimeDiagnosticEventSink, IDisposable
{
    private readonly IAppLogger _logger;
    private readonly IAppPathProvider _paths;
    private readonly IJsonFileStore _jsonFileStore;
    private readonly IAgentRunContextAccessor _runContextAccessor;
    private readonly IActiveAgentSessionContext? _activeSessionContext;

    private readonly Channel<RuntimeDiagnosticEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    private long _sequence;
    private int _pendingWrites;
    private const string GlobalSessionKey = "__logs__";
    private readonly Dictionary<string, IndexState> _indexStates = [];

    public RuntimeDiagnosticEventSink(
        IAppLogger logger,
        IAppPathProvider paths,
        IJsonFileStore jsonFileStore,
        IAgentRunContextAccessor runContextAccessor,
        IActiveAgentSessionContext? activeSessionContext = null,
        int boundedCapacity = 10_000)
    {
        _logger = logger.ForContext("RuntimeDiagnosticEventSink");
        _paths = paths;
        _jsonFileStore = jsonFileStore;
        _runContextAccessor = runContextAccessor;
        _activeSessionContext = activeSessionContext;

        _channel = Channel.CreateBounded<RuntimeDiagnosticEvent>(new BoundedChannelOptions(boundedCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _workerTask = Task.Run(ProcessQueueAsync);
    }

           public ValueTask EnqueueAsync(RuntimeDiagnosticEvent evt, CancellationToken cancellationToken = default)
    {
        // Instrumentation should pass a fully formed event, but we still harden for partial writes.
        if (string.IsNullOrWhiteSpace(evt.sessionId))
        {
            var inferredSessionId = _runContextAccessor.Current?.SessionId ?? _activeSessionContext?.SessionId;
            if (!string.IsNullOrWhiteSpace(inferredSessionId))
            {
                evt = evt with { sessionId = inferredSessionId };
            }
        }

        if (string.IsNullOrWhiteSpace(evt.eventId))
        {
            evt = evt with { eventId = Guid.NewGuid().ToString("N") };
        }

        if (evt.ts == default)
        {
            evt = evt with { ts = AppTimeZone.Now };
        }

        if (evt.sequence <= 0)
        {
            var seq = Interlocked.Increment(ref _sequence);
            evt = evt with { sequence = seq };
        }

        Interlocked.Increment(ref _pendingWrites);
        if (_channel.Writer.TryWrite(evt))
        {
            return ValueTask.CompletedTask;
        }

        // Dropped by channel policy.
        Interlocked.Decrement(ref _pendingWrites);
        return ValueTask.CompletedTask;
    }

           public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _pendingWrites) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
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
                    var path = ResolveRuntimeEventsPath(evt.sessionId);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await _jsonFileStore.AppendJsonLineAsync(path, evt, cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);

                    // Best-effort diagnostic index for faster triage.
                    // Keep this out of the main correctness path: any failures are swallowed.
                    try
                    {
                        UpdateIndexAndPersist(path, evt);
                    }
                    catch (Exception)
                    {
                        // ignore index write failures
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "Failed to write runtime diagnostic event: {EventType} {Error}",
                        evt.eventType,
                        ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingWrites);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore shutdown
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RuntimeDiagnosticEventSink worker failed");
        }
    }

    private string ResolveRuntimeEventsPath(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Path.Combine(_paths.LogsPath, "runtime-events.jsonl");
        }

        var resolved = _runContextAccessor.ResolveSessionDirectory(_paths.SessionsPath, sessionId);

        if (_runContextAccessor.Current?.Kind == AgentRunKind.SubAgent)
        {
            return Path.Combine(resolved, "diagnostics", "runtime-events.jsonl");
        }

        if (SessionDirectoryLayout.IsTopLevelSessionDirectory(_paths.SessionsPath, resolved)
            && SessionDirectoryLayout.TryFindNestedSubAgentDirectory(_paths.SessionsPath, sessionId) is { } nested)
        {
            resolved = nested;
        }

        return Path.Combine(resolved, "diagnostics", "runtime-events.jsonl");
    }

    private void UpdateIndexAndPersist(string runtimeEventsPath, RuntimeDiagnosticEvent evt)
    {
        var directory = Path.GetDirectoryName(runtimeEventsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var indexPath = Path.Combine(directory, "diagnostic-index.json");

        var stateKey = evt.sessionId ?? GlobalSessionKey;
        var state = _indexStates.GetValueOrDefault(stateKey);
        if (state is null)
        {
            state = new IndexState();
            _indexStates[stateKey] = state;
        }

        state.TotalEvents++;

        if (!string.IsNullOrWhiteSpace(evt.errorCode))
        {
            state.ErrorCodeCounts.TryGetValue(evt.errorCode!, out var current);
            state.ErrorCodeCounts[evt.errorCode!] = current + 1;

            // Track the most recent error-ish event with an errorCode.
            if (evt.severity is RuntimeDiagnosticSeverity.Error or RuntimeDiagnosticSeverity.Critical
                || state.LastFailed is null)
            {
                state.LastFailed = new LastFailedEvent(
                    ts: evt.ts == default ? AppTimeZone.Now : evt.ts,
                    errorCode: evt.errorCode,
                    component: evt.component,
                    phase: evt.phase,
                    severity: evt.severity,
                    eventType: evt.eventType,
                    message: evt.message);
            }
        }

        state.UpdatedAt = AppTimeZone.Now;

        var index = new DiagnosticIndex(
            sessionId: evt.sessionId,
            updatedAt: state.UpdatedAt,
            totalEvents: state.TotalEvents,
            lastFailed: state.LastFailed,
            errorCodeCounts: state.ErrorCodeCounts);

        var json = JsonSerializer.Serialize(index, Athlon.Agent.Infrastructure.JsonFileStore.Options);
        File.WriteAllText(indexPath, json);
    }

    private sealed class IndexState
    {
        public long TotalEvents { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public LastFailedEvent? LastFailed { get; set; }
        public Dictionary<string, int> ErrorCodeCounts { get; } = new(StringComparer.Ordinal);
    }

    private sealed record LastFailedEvent(
        DateTimeOffset ts,
        string? errorCode,
        RuntimeDiagnosticComponent component,
        RuntimeDiagnosticPhase phase,
        RuntimeDiagnosticSeverity severity,
        string eventType,
        string? message);

    private sealed record DiagnosticIndex(
        string? sessionId,
        DateTimeOffset updatedAt,
        long totalEvents,
        LastFailedEvent? lastFailed,
        Dictionary<string, int> errorCodeCounts);

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _workerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore dispose races
        }

        _cts.Dispose();
    }
}

