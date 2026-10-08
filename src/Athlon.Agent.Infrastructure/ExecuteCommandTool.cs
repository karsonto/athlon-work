using System.Diagnostics;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class ExecuteCommandTool(
    AppSettings settings,
    WorkspaceGuard guard,
    AuditLogService audit,
    ExecuteCommandProcessRegistry processRegistry,
    BackgroundCommandRegistry backgroundCommands,
    IActiveAgentSessionContext sessionContext) : IAgentTool, ILocalWorkspaceTool
{
    public const int DefaultTimeoutSeconds = 3600;
    public const int MaxTimeoutSeconds = 3600;
    public const int MaxCapturedOutputChars = 200_000;

    public ToolDefinition Definition { get; } = new(
        "execute_command",
        "Execute a shell command (user approval required). On Windows use cmd.exe semantics, not PowerShell. "
            + "After code changes, verify with project-appropriate checks (e.g. mvn -q -pl <module> compile, npx eslint <path>, pytest <test file>) on only the files you changed. "
            + "Console I/O prefers UTF-8 (chcp 65001) and normalizes captured output to UTF-8. "
            + $"block_until_ms (default {BackgroundCommandRegistry.DefaultBlockUntilMs}, 0 = return as soon as the process starts) only returns control. "
            + "A still-running process stays in the background until it exits, timeout, or command_kill. "
        + $"Default timeout {DefaultTimeoutSeconds}s (max {MaxTimeoutSeconds}s) stops the process; it does not end the agent turn.",
        ToolSchema.Object()
            .String("command", "Command line (quote paths that contain spaces or non-ASCII characters)", required: true, minLength: 1)
            .String("cwd", ToolPathDescriptions.OptionalWorkspaceRelativeCwd)
            .Integer("timeout", $"Hard stop in seconds (default {DefaultTimeoutSeconds}, max {MaxTimeoutSeconds})", defaultValue: DefaultTimeoutSeconds, minimum: 1, maximum: MaxTimeoutSeconds)
            .Integer(
                "block_until_ms",
                $"How long to wait before returning control (default {BackgroundCommandRegistry.DefaultBlockUntilMs}, 0 = background immediately). Does not stop the process.",
                defaultValue: BackgroundCommandRegistry.DefaultBlockUntilMs,
                minimum: 0,
                maximum: BackgroundCommandRegistry.MaxBlockUntilMs)
            .Build(),
        RequiresApproval: true,
        Group: ToolGroup.Builtin,
        MaxOutputChars: MaxCapturedOutputChars,
        InvocationPolicy: ToolInvocationPolicy.Ask);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "command", out var command, out var error))
        {
            return error;
        }

        if (CommandDenyListMatcher.IsDenied(command, settings.ToolPermissions.CommandDenyList))
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
        var blockUntilMs = Math.Clamp(
            ToolArguments.GetInt32(invocation, "block_until_ms", BackgroundCommandRegistry.DefaultBlockUntilMs),
            0,
            BackgroundCommandRegistry.MaxBlockUntilMs);
        blockUntilMs = Math.Min(blockUntilMs, timeoutSeconds * 1000);

        var sessionId = string.IsNullOrWhiteSpace(sessionContext.SessionId) ? "local" : sessionContext.SessionId;
        if (backgroundCommands.RunningCount(sessionId) >= BackgroundCommandRegistry.MaxPerSession)
        {
            return ToolResult.Failure(
                "Too many background commands",
                $"This session already has {BackgroundCommandRegistry.MaxPerSession} background commands. Call command_kill before starting another.");
        }

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
        var killRegistration = cancellationToken.Register(() => ProcessKillHelper.KillProcessTree(process));
        var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var blockTimeout = new CancellationTokenSource();
        var mirrorOutput = 1;
        var stdout = new SharedOutputBuffer(MaxCapturedOutputChars);
        var stderr = new SharedOutputBuffer(MaxCapturedOutputChars);
        var stdoutTask = ReadStreamAsync(process.StandardOutput.BaseStream, stdout, () => Volatile.Read(ref mirrorOutput) == 1);
        var stderrTask = ReadStreamAsync(process.StandardError.BaseStream, stderr, () => Volatile.Read(ref mirrorOutput) == 1);
        var backgrounded = false;
        var blockDelay = Task.Delay(blockUntilMs, blockTimeout.Token);

        var sw = Stopwatch.StartNew();
        try
        {
            var userDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var userRegistration = cancellationToken.Register(() => userDone.TrySetResult());
            var hardDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var hardRegistration = hardTimeout.Token.Register(() => hardDone.TrySetResult());
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAny(exitTask, blockDelay, hardDone.Task, userDone.Task).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                ProcessKillHelper.KillProcessTree(process);
                throw new OperationCanceledException(cancellationToken);
            }

            if (process.HasExited)
            {
                await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                var content = SharedOutputBuffer.Combine(stdout.ToString(), stderr.ToString());
                var exitCode = process.ExitCode;
                await audit.WriteAsync(
                    "execute_command",
                    new { command, cwd, exitCode, elapsedMs = sw.ElapsedMilliseconds },
                    CancellationToken.None).ConfigureAwait(false);
                return exitCode == 0
                    ? ToolResult.Success($"Command exited 0 in {sw.ElapsedMilliseconds}ms", content, sw.Elapsed)
                    : ToolResult.Failure("Command failed", content, sw.Elapsed);
            }

            if (hardTimeout.IsCancellationRequested)
            {
                ProcessKillHelper.KillProcessTree(process);
                await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                var partial = SharedOutputBuffer.Combine(stdout.ToString(), stderr.ToString());
                var timeoutMessage = $"Command exceeded {timeoutSeconds}s timeout.";
                await audit.WriteAsync(
                    "execute_command",
                    new { command, cwd, timedOut = true, elapsedMs = sw.ElapsedMilliseconds },
                    CancellationToken.None).ConfigureAwait(false);
                return ToolResult.Failure(
                    "Command timed out",
                    string.IsNullOrWhiteSpace(partial) ? timeoutMessage : timeoutMessage + Environment.NewLine + partial,
                    sw.Elapsed);
            }

            killRegistration.Dispose();
            Volatile.Write(ref mirrorOutput, 0);
            var commandId = IdGen.NewId();
            backgrounded = backgroundCommands.TryAdopt(new BackgroundCommandStart
            {
                CommandId = commandId,
                SessionId = sessionId,
                Process = process,
                Stdout = stdout,
                Stderr = stderr,
                StdoutTask = stdoutTask,
                StderrTask = stderrTask,
                HardTimeout = hardTimeout
            });
            if (!backgrounded)
            {
                ProcessKillHelper.KillProcessTree(process);
                return ToolResult.Failure(
                    "Too many background commands",
                    $"This session already has {BackgroundCommandRegistry.MaxPerSession} background commands. Call command_kill before starting another.");
            }

            var output = SharedOutputBuffer.Combine(stdout.ToString(), stderr.ToString());
            var body =
                $"command_id: {commandId}\n" +
                $"pid: {process.Id}\n" +
                "status: running\n" +
                "The process is still running. The exit code is not known yet.\n" +
                "Call command_await to wait or read output, or command_kill to stop it.\n" +
                (string.IsNullOrWhiteSpace(output) ? "(no output yet)" : output);
            await audit.WriteAsync(
                "execute_command",
                new { command, cwd, backgrounded = true, commandId, pid = process.Id, elapsedMs = sw.ElapsedMilliseconds },
                CancellationToken.None).ConfigureAwait(false);
            return ToolResult.Success("Command is running in the background", body, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            ProcessKillHelper.KillProcessTree(process);
            throw;
        }
        finally
        {
            killRegistration.Dispose();
            if (!BlockDelayCompleted(blockDelay))
            {
                blockTimeout.Cancel();
            }

            Observe(blockDelay);
            blockTimeout.Dispose();
            if (!backgrounded)
            {
                processRegistry.Unregister(process);
                hardTimeout.Cancel();
                hardTimeout.Dispose();
            }
        }
    }

    private static bool BlockDelayCompleted(Task blockDelay) =>
        blockDelay.IsCompleted && !blockDelay.IsCanceled && !blockDelay.IsFaulted;

    private static void Observe(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task DrainAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Keep the output captured so far.
        }
    }

    private static Task ReadStreamAsync(Stream stream, SharedOutputBuffer accumulator, Func<bool> mirror) =>
        ProcessConsoleStreamReader.ReadLinesAsync(
            stream,
            line =>
            {
                var captured = accumulator.AppendLine(line);
                if (captured && mirror())
                {
                    AmbientToolOutputStream.CurrentStream?.WriteLine(line);
                }
            },
            CancellationToken.None);
}
