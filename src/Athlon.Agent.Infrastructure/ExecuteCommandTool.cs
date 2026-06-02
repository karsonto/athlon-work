using System.Diagnostics;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class ExecuteCommandTool(
    AppSettings settings,
    AuditLogService audit,
    ExecuteCommandProcessRegistry processRegistry) : IAgentTool
{
    public const int DefaultTimeoutSeconds = 3600;
    public const int MaxTimeoutSeconds = 3600;

    public ToolDefinition Definition { get; } = new(
        "execute_command",
        "Execute a shell command (user approval required). On Windows use cmd.exe semantics, not PowerShell. "
        + $"Default timeout {DefaultTimeoutSeconds}s (max {MaxTimeoutSeconds}s); timeout ends only this tool, not the agent turn.",
        new Dictionary<string, string>
        {
            ["command"] = "Command line",
            ["cwd"] = "Working directory",
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

        var cwd = invocation.Arguments.GetValueOrDefault("cwd") ?? Environment.CurrentDirectory;
        if (!Directory.Exists(cwd))
        {
            return ToolResult.Failure(
                "Invalid working directory",
                $"Working directory does not exist: {cwd}");
        }

        var timeoutSeconds = Math.Clamp(
            ToolArguments.GetInt32(invocation, "timeout", DefaultTimeoutSeconds),
            1,
            MaxTimeoutSeconds);

        var startInfo = new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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
            await audit.WriteAsync(
                "execute_command",
                new { command, cwd, process.ExitCode, elapsedMs = sw.ElapsedMilliseconds },
                cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(userCancelToken);
        var stderrTask = process.StandardError.ReadToEndAsync(userCancelToken);
        var exitTask = process.WaitForExitAsync(userCancelToken);
        var runTask = Task.WhenAll(stdoutTask, stderrTask, exitTask);
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

        var winner = await Task.WhenAny(runTask, timeoutTask).ConfigureAwait(false);
        if (winner == timeoutTask)
        {
            userCancelToken.ThrowIfCancellationRequested();
            return new ProcessRunResult(null, null, TimedOut: true);
        }

        await runTask.ConfigureAwait(false);
        return new ProcessRunResult(await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), TimedOut: false);
    }

    private static string FormatOutput(string? stdout, string? stderr) =>
        string.IsNullOrWhiteSpace(stderr)
            ? stdout ?? string.Empty
            : (stdout ?? string.Empty) + Environment.NewLine + stderr;

    private sealed record ProcessRunResult(string? Stdout, string? Stderr, bool TimedOut);
}
