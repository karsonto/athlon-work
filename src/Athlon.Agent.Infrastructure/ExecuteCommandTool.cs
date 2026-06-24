using System.Diagnostics;
using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class ExecuteCommandTool(
    AppSettings settings,
    WorkspaceGuard guard,
    AuditLogService audit,
    ExecuteCommandProcessRegistry processRegistry) : IAgentTool
{
    public const int DefaultTimeoutSeconds = 3600;
    public const int MaxTimeoutSeconds = 3600;
    public const int MaxCapturedOutputChars = 200_000;

    public ToolDefinition Definition { get; } = new(
        "execute_command",
        "Execute a shell command (user approval required). On Windows use cmd.exe semantics, not PowerShell. "
        + "Console I/O uses UTF-8 (chcp 65001). "
        + $"Default timeout {DefaultTimeoutSeconds}s (max {MaxTimeoutSeconds}s); timeout ends only this tool, not the agent turn.",
        new Dictionary<string, string>
        {
            ["command"] = "Command line (quote paths that contain spaces or non-ASCII characters)",
            ["cwd"] = ToolPathDescriptions.OptionalWorkspaceRelativeCwd,
            ["timeout"] = $"Timeout in seconds (default {DefaultTimeoutSeconds}, max {MaxTimeoutSeconds})"
        },
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "command", out var command, out var error))
        {
            return error;
        }

        if (settings.ToolPermissions.CommandDenyList.Any(deny => command.Contains(deny, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult.Failure("Command denied", command);
        }

        if (!ToolArguments.TryResolveWorkingDirectory(invocation, guard, out var cwd, out error))
        {
            return error;
        }

        var timeoutSeconds = Math.Clamp(
            ToolArguments.GetInt32(invocation, "timeout", DefaultTimeoutSeconds),
            1,
            MaxTimeoutSeconds);

        var startInfo = new ProcessStartInfo("cmd.exe", "/c " + WindowsCmdEncoding.WrapCommandForUtf8(command))
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        WindowsCmdEncoding.ApplyTo(startInfo);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Failed to start process", ex.Message);
        }

        processRegistry.Register(process);
        using var killRegistration = cancellationToken.Register(() => ProcessKillHelper.KillProcessTree(process));

        var sw = Stopwatch.StartNew();
        try
        {
            var run = await WaitForProcessAsync(process, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (run.TimedOut)
            {
                ProcessKillHelper.KillProcessTree(process);
                var partial = FormatOutput(run.Stdout, run.Stderr);
                var timeoutMessage = $"Command exceeded {timeoutSeconds}s timeout.";
                await audit.WriteAsync(
                    "execute_command",
                    new { command, cwd, timedOut = true, elapsedMs = sw.ElapsedMilliseconds },
                    cancellationToken).ConfigureAwait(false);
                return ToolResult.Failure(
                    "Command timed out",
                    string.IsNullOrWhiteSpace(partial) ? timeoutMessage : timeoutMessage + Environment.NewLine + partial,
                    sw.Elapsed);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var content = FormatOutput(run.Stdout, run.Stderr);
            var exitCode = process.HasExited ? process.ExitCode : -1;
            await audit.WriteAsync(
                "execute_command",
                new { command, cwd, exitCode, elapsedMs = sw.ElapsedMilliseconds },
                cancellationToken).ConfigureAwait(false);
            return exitCode == 0
                ? ToolResult.Success($"Command exited 0 in {sw.ElapsedMilliseconds}ms", content, sw.Elapsed)
                : ToolResult.Failure("Command failed", content, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            ProcessKillHelper.KillProcessTree(process);
            throw;
        }
        finally
        {
            processRegistry.Unregister(process);
        }
    }

    private static async Task<ProcessRunResult> WaitForProcessAsync(
        Process process,
        int timeoutSeconds,
        CancellationToken userCancelToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(userCancelToken, timeoutCts.Token).Token;

        var stdoutAccum = new BoundedOutputBuffer(MaxCapturedOutputChars);
        var stderrAccum = new BoundedOutputBuffer(MaxCapturedOutputChars);

        var stdoutTask = ReadStdoutLinesAsync(process, stdoutAccum, combinedToken);
        var stderrTask = ReadStderrLinesAsync(process, stderrAccum, combinedToken);
        var exitTask = process.WaitForExitAsync(combinedToken);
        var runTask = Task.WhenAll(stdoutTask, stderrTask, exitTask);
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

        var winner = await Task.WhenAny(runTask, timeoutTask).ConfigureAwait(false);
        if (winner == timeoutTask)
        {
            userCancelToken.ThrowIfCancellationRequested();
            return new ProcessRunResult(null, null, TimedOut: true);
        }

        await runTask.ConfigureAwait(false);
        return new ProcessRunResult(stdoutAccum.ToString(), stderrAccum.ToString(), TimedOut: false);
    }

    /// <summary>Reads stdout line by line, accumulating and pushing each line through the ambient output stream.</summary>
    private static async Task ReadStdoutLinesAsync(
        Process process,
        BoundedOutputBuffer accumulator,
        CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                var captured = accumulator.AppendLine(line);
                if (captured)
                {
                    AmbientToolOutputStream.CurrentStream?.WriteLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user cancels or timeout fires — stop reading.
        }
    }

    /// <summary>Reads stderr line by line, same streaming pattern as stdout.</summary>
    private static async Task ReadStderrLinesAsync(
        Process process,
        BoundedOutputBuffer accumulator,
        CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                var captured = accumulator.AppendLine(line);
                if (captured)
                {
                    AmbientToolOutputStream.CurrentStream?.WriteLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user cancels or timeout fires — stop reading.
        }
    }

    private static string FormatOutput(string? stdout, string? stderr) =>
        string.IsNullOrWhiteSpace(stderr)
            ? stdout ?? string.Empty
            : (stdout ?? string.Empty) + Environment.NewLine + stderr;

    private sealed record ProcessRunResult(string? Stdout, string? Stderr, bool TimedOut);

    private sealed class BoundedOutputBuffer(int maxChars)
    {
        private readonly StringBuilder _builder = new();
        private bool _truncated;

        public bool AppendLine(string line)
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

        public override string ToString() => _builder.ToString();
    }
}
