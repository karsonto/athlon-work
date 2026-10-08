using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class BackgroundCommandRegistry(ExecuteCommandProcessRegistry processRegistry)
{
    public const int DefaultBlockUntilMs = 30_000;
    public const int MaxBlockUntilMs = 3_600_000;
    public const int MaxPerSession = 8;
    public const int OutputTailChars = 4_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<BackgroundCommandHandle>> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<BackgroundCommandCompletion>> _completions = new(StringComparer.Ordinal);
    private IBackgroundCommandCompletionNotifier? _notifier;

    public void SetNotifier(IBackgroundCommandCompletionNotifier? notifier) => _notifier = notifier;

    public int RunningCount(string sessionId)
    {
        lock (_gate)
        {
            return _running.TryGetValue(sessionId, out var handles)
                ? handles.Count(handle => !handle.IsFinished)
                : 0;
        }
    }

    public int PeekCompletionCount(string sessionId)
    {
        lock (_gate)
        {
            return _completions.TryGetValue(sessionId, out var queued) ? queued.Count : 0;
        }
    }

    internal bool TryAdopt(BackgroundCommandStart start)
    {
        var handle = new BackgroundCommandHandle(start);
        lock (_gate)
        {
            if (RunningCountUnlocked(start.SessionId) >= MaxPerSession)
            {
                return false;
            }

            if (!_running.TryGetValue(start.SessionId, out var handles))
            {
                handles = [];
                _running[start.SessionId] = handles;
            }

            handles.Add(handle);
        }

        _ = WatchAsync(handle);
        return true;
    }

    public IReadOnlyList<BackgroundCommandCompletion> DrainCompletions(string sessionId)
    {
        lock (_gate)
        {
            if (!_completions.TryGetValue(sessionId, out var queued) || queued.Count == 0)
            {
                return [];
            }

            var drained = queued.ToArray();
            queued.Clear();
            return drained;
        }
    }

    public async Task<BackgroundCommandSnapshot?> AwaitAsync(
        string sessionId,
        string commandId,
        int blockUntilMs,
        string? pattern,
        CancellationToken cancellationToken)
    {
        var handle = Find(sessionId, commandId);
        if (handle is null)
        {
            return null;
        }

        Regex? regex = null;
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            try
            {
                regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidBackgroundCommandPatternException(ex.Message);
            }
        }

        var deadline = Environment.TickCount64 + Math.Max(0, blockUntilMs);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = handle.Snapshot();
            if (snapshot.Running == false || (regex is not null && IsMatch(regex, snapshot.Output)))
            {
                return handle.Snapshot();
            }

            var remaining = deadline - Environment.TickCount64;
            if (blockUntilMs <= 0 || remaining <= 0)
            {
                return snapshot;
            }

            var delay = (int)Math.Min(remaining, 200);
            await Task.WhenAny(handle.Finished.Task, Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
        }
    }

    public BackgroundCommandSnapshot? TryGet(string sessionId, string commandId) =>
        Find(sessionId, commandId)?.Snapshot();

    public bool TryKill(string sessionId, string commandId)
    {
        var handle = Find(sessionId, commandId);
        if (handle is null || handle.IsFinished)
        {
            return false;
        }

        handle.RequestKill();
        ProcessKillHelper.KillProcessTree(handle.Process);
        return true;
    }

    private async Task WatchAsync(BackgroundCommandHandle handle)
    {
        var timedOut = false;
        try
        {
            await handle.Process.WaitForExitAsync(handle.HardTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            ProcessKillHelper.KillProcessTree(handle.Process);
            try
            {
                await handle.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The process is already gone.
            }
        }
        catch (Exception)
        {
            ProcessKillHelper.KillProcessTree(handle.Process);
        }

        try
        {
            await Task.WhenAll(handle.StdoutTask, handle.StderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Keep the output captured so far.
        }

        int? exitCode = null;
        try
        {
            if (handle.Process.HasExited)
            {
                exitCode = handle.Process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            exitCode = null;
        }

        var killed = handle.KillRequested && !timedOut;
        var output = handle.CombinedOutput();
        var status = timedOut ? "timed_out" : killed ? "killed" : "exited";
        var tail = output.Length <= OutputTailChars ? output : output[^OutputTailChars..];
        var announce =
            $"Background command {status}.\n" +
            $"command_id: {handle.CommandId}\n" +
            $"pid: {handle.Pid}\n" +
            $"exit_code: {exitCode?.ToString() ?? "unknown"}\n" +
            "output_tail:\n" +
            (string.IsNullOrWhiteSpace(tail) ? "(no output)" : tail);
        var completion = new BackgroundCommandCompletion(handle.CommandId, handle.Pid, exitCode, announce);

        lock (_gate)
        {
            if (!_completions.TryGetValue(handle.SessionId, out var queued))
            {
                queued = new Queue<BackgroundCommandCompletion>();
                _completions[handle.SessionId] = queued;
            }

            queued.Enqueue(completion);
        }

        handle.MarkFinished(exitCode, timedOut, killed);
        TrimFinished(handle.SessionId);
        processRegistry.Unregister(handle.Process);
        try
        {
            handle.HardTimeout.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already released.
        }

        handle.HardTimeout.Dispose();
        handle.Process.Dispose();
        _notifier?.NotifyCompletionReady(handle.SessionId);
    }

    private BackgroundCommandHandle? Find(string sessionId, string commandId)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(sessionId, out var handles))
            {
                return null;
            }

            return handles.FirstOrDefault(handle => string.Equals(handle.CommandId, commandId, StringComparison.Ordinal));
        }
    }

    private void TrimFinished(string sessionId)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(sessionId, out var handles))
            {
                return;
            }

            var finished = handles.Where(handle => handle.IsFinished).ToList();
            if (finished.Count <= 32)
            {
                return;
            }

            foreach (var old in finished.Take(finished.Count - 32))
            {
                handles.Remove(old);
            }
        }
    }

    private int RunningCountUnlocked(string sessionId) =>
        _running.TryGetValue(sessionId, out var handles)
            ? handles.Count(handle => !handle.IsFinished)
            : 0;

    private static bool IsMatch(Regex regex, string output)
    {
        try
        {
            return regex.IsMatch(output);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

public sealed class InvalidBackgroundCommandPatternException(string message) : Exception(message);

internal sealed class BackgroundCommandStart
{
    public required string CommandId { get; init; }
    public required string SessionId { get; init; }
    public required Process Process { get; init; }
    public required SharedOutputBuffer Stdout { get; init; }
    public required SharedOutputBuffer Stderr { get; init; }
    public required Task StdoutTask { get; init; }
    public required Task StderrTask { get; init; }
    public required CancellationTokenSource HardTimeout { get; init; }
}

public sealed class BackgroundCommandSnapshot
{
    public required string CommandId { get; init; }
    public required int Pid { get; init; }
    public required bool Running { get; init; }
    public int? ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Killed { get; init; }
    public required string Output { get; init; }
}

internal sealed class BackgroundCommandHandle(BackgroundCommandStart start)
{
    public string CommandId { get; } = start.CommandId;
    public string SessionId { get; } = start.SessionId;
    public int Pid { get; } = start.Process.Id;
    public Process Process { get; } = start.Process;
    public SharedOutputBuffer Stdout { get; } = start.Stdout;
    public SharedOutputBuffer Stderr { get; } = start.Stderr;
    public Task StdoutTask { get; } = start.StdoutTask;
    public Task StderrTask { get; } = start.StderrTask;
    public CancellationTokenSource HardTimeout { get; } = start.HardTimeout;
    public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _killRequested;
    private int? _exitCode;
    private bool _timedOut;
    private bool _killed;

    public bool IsFinished => Finished.Task.IsCompleted;
    public bool KillRequested => Volatile.Read(ref _killRequested) == 1;

    public void RequestKill() => Interlocked.Exchange(ref _killRequested, 1);

    public void MarkFinished(int? exitCode, bool timedOut, bool killed)
    {
        _exitCode = exitCode;
        _timedOut = timedOut;
        _killed = killed;
        Finished.TrySetResult(true);
    }

    public string CombinedOutput() => SharedOutputBuffer.Combine(Stdout.ToString(), Stderr.ToString());

    public BackgroundCommandSnapshot Snapshot() => new()
    {
        CommandId = CommandId,
        Pid = Pid,
        Running = !IsFinished,
        ExitCode = _exitCode,
        TimedOut = _timedOut,
        Killed = _killed,
        Output = CombinedOutput()
    };
}

internal sealed class SharedOutputBuffer(int maxChars)
{
    private readonly StringBuilder _builder = new();
    private bool _truncated;

    public bool AppendLine(string line)
    {
        lock (_builder)
        {
            if (_truncated)
            {
                return false;
            }

            var remaining = maxChars - _builder.Length;
            if (remaining <= 0)
            {
                AppendTruncationNotice();
                return false;
            }

            var lineWithNewLine = line + Environment.NewLine;
            if (lineWithNewLine.Length <= remaining)
            {
                _builder.Append(lineWithNewLine);
                return true;
            }

            _builder.Append(lineWithNewLine.AsSpan(0, Math.Max(0, remaining)));
            AppendTruncationNotice();
            return false;
        }
    }

    public override string ToString()
    {
        lock (_builder)
        {
            return _builder.ToString();
        }
    }

    public static string Combine(string? stdout, string? stderr) =>
        string.IsNullOrWhiteSpace(stderr)
            ? stdout ?? string.Empty
            : (stdout ?? string.Empty) + Environment.NewLine + stderr;

    private void AppendTruncationNotice()
    {
        if (_truncated)
        {
            return;
        }

        _truncated = true;
        _builder.AppendLine();
        _builder.AppendLine($"[Output truncated after {maxChars} characters. Redirect large output to a file and inspect it with file tools.]");
    }
}
