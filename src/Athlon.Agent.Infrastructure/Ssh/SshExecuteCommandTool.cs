using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshExecuteCommandTool(
    AppSettings settings,
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit) : IAgentTool, IRemoteWorkspaceTool
{
    public const int DefaultTimeoutSeconds = ExecuteCommandTool.DefaultTimeoutSeconds;
    public const int MaxTimeoutSeconds = ExecuteCommandTool.MaxTimeoutSeconds;
    public const int MaxCapturedOutputChars = ExecuteCommandTool.MaxCapturedOutputChars;

    public ToolDefinition Definition { get; } = new(
        "execute_command",
        "Execute a shell command on the remote SSH workspace (user approval required). "
            + "Working directory defaults to the remote workspace root. "
            + $"Default timeout {DefaultTimeoutSeconds}s (max {MaxTimeoutSeconds}s); timeout ends only this tool, not the agent turn.",
        ToolSchema.Object()
            .String("command", "Command line (quote paths that contain spaces)", required: true, minLength: 1)
            .String("cwd", ToolPathDescriptions.OptionalWorkspaceRelativeCwd)
            .Integer("timeout", $"Timeout in seconds (default {DefaultTimeoutSeconds}, max {MaxTimeoutSeconds})", defaultValue: DefaultTimeoutSeconds, minimum: 1, maximum: MaxTimeoutSeconds)
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

        if (!SshWorkspaceToolHelper.TryEnsureConnected(client, out error))
        {
            return error;
        }

        if (!TryResolveRemoteWorkingDirectory(invocation, out var cwd, out error))
        {
            return error;
        }

        try
        {
            var cwdInfo = await client.TryGetFileInfoAsync(cwd, cancellationToken).ConfigureAwait(false);
            if (cwdInfo is null)
            {
                return ToolResult.Failure("Invalid working directory", $"Working directory does not exist: {cwd}");
            }

            if (!cwdInfo.IsDirectory)
            {
                return ToolResult.Failure("Invalid working directory", $"Working directory is not a directory: {cwd}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Invalid working directory", ex.Message);
        }

        var timeoutSeconds = Math.Clamp(
            ToolArguments.GetInt32(invocation, "timeout", DefaultTimeoutSeconds),
            1,
            MaxTimeoutSeconds);

        try
        {
            var result = await client.ExecuteAsync(
                    command,
                    cwd,
                    TimeSpan.FromSeconds(timeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);

            var output = CombineOutput(result.StdOut, result.StdErr);
            if (output.Length > MaxCapturedOutputChars)
            {
                output = output[..MaxCapturedOutputChars] + "\n... [output truncated]";
            }

            await WorkspaceToolHelper.AuditAsync(
                audit,
                "execute_command",
                new
                {
                    command,
                    cwd,
                    exitCode = result.ExitCode,
                    durationMs = (int)result.Duration.TotalMilliseconds,
                    remote = true
                },
                cancellationToken).ConfigureAwait(false);

            var summary = result.ExitCode == 0
                ? $"Command succeeded (exit {result.ExitCode})"
                : $"Command failed (exit {result.ExitCode})";
            return result.ExitCode == 0
                ? ToolResult.Success(summary, output, result.Duration)
                : ToolResult.Failure(summary, string.IsNullOrWhiteSpace(output) ? summary : output, result.Duration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Command failed", ex.Message);
        }
    }

    private bool TryResolveRemoteWorkingDirectory(ToolInvocation invocation, out string fullPath, out ToolResult error)
    {
        if (!invocation.Arguments.TryGetString(ToolPathNormalizer.CwdArgumentName, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            fullPath = guard.Normalize(".");
            error = ToolResult.Success("OK");
            return true;
        }

        if (!ToolPathNormalizer.TryNormalizeForFileOperation(raw, out var normalized, out var message))
        {
            fullPath = string.Empty;
            error = ToolResult.Failure("Invalid working directory", $"{invocation.ToolName}: {message}");
            return false;
        }

        fullPath = guard.Normalize(normalized);
        if (guard.HasConfiguredWorkspace && !guard.IsInsideWorkspace(fullPath))
        {
            error = ToolResult.Failure("Outside workspace", fullPath);
            fullPath = string.Empty;
            return false;
        }

        error = ToolResult.Success("OK");
        return true;
    }

    private static string CombineOutput(string stdout, string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return stdout ?? string.Empty;
        }

        if (string.IsNullOrEmpty(stdout))
        {
            return stderr;
        }

        return stdout.TrimEnd() + Environment.NewLine + stderr;
    }
}
