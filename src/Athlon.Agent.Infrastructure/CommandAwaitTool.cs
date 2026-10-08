using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class CommandAwaitTool(
    BackgroundCommandRegistry backgroundCommands,
    IActiveAgentSessionContext sessionContext) : IAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "command_await",
        "Wait for a background execute_command process or read its output. "
            + "block_until_ms 0 returns the current status immediately. "
            + "Optional pattern returns early when it matches the combined output.",
        ToolSchema.Object()
            .String("command_id", "command_id returned when the command was backgrounded", required: true, minLength: 1)
            .Integer(
                "block_until_ms",
                $"How long to wait in milliseconds (default {BackgroundCommandRegistry.DefaultBlockUntilMs}, 0 = status only)",
                defaultValue: BackgroundCommandRegistry.DefaultBlockUntilMs,
                minimum: 0,
                maximum: BackgroundCommandRegistry.MaxBlockUntilMs)
            .String("pattern", "Optional regular expression matched against the combined output")
            .Build(),
        RequiresApproval: false,
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "command_id", out var commandId, out var error))
        {
            return error;
        }

        var sessionId = string.IsNullOrWhiteSpace(sessionContext.SessionId) ? "local" : sessionContext.SessionId;
        var blockUntilMs = Math.Clamp(
            ToolArguments.GetInt32(invocation, "block_until_ms", BackgroundCommandRegistry.DefaultBlockUntilMs),
            0,
            BackgroundCommandRegistry.MaxBlockUntilMs);
        var pattern = invocation.Arguments.GetString("pattern");

        BackgroundCommandSnapshot? snapshot;
        try
        {
            snapshot = await backgroundCommands.AwaitAsync(
                sessionId,
                commandId,
                blockUntilMs,
                pattern,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidBackgroundCommandPatternException ex)
        {
            return ToolResult.Failure("Invalid pattern", ex.Message);
        }

        if (snapshot is null)
        {
            return ToolResult.Failure(
                "Command not found",
                $"No background command {commandId} is tracked for this session.");
        }

        var status = snapshot.Running
            ? "running"
            : snapshot.TimedOut
                ? "timed_out"
                : snapshot.Killed
                    ? "killed"
                    : "exited";
        var body =
            $"command_id: {snapshot.CommandId}\n" +
            $"pid: {snapshot.Pid}\n" +
            $"status: {status}\n" +
            $"exit_code: {snapshot.ExitCode?.ToString() ?? "unknown"}\n" +
            (string.IsNullOrWhiteSpace(snapshot.Output) ? "(no output)" : snapshot.Output);
        return snapshot.Running
            ? ToolResult.Success("Command is still running", body)
            : snapshot.ExitCode == 0 && !snapshot.TimedOut && !snapshot.Killed
                ? ToolResult.Success($"Command exited 0", body)
                : ToolResult.Failure(snapshot.TimedOut ? "Command timed out" : snapshot.Killed ? "Command killed" : "Command failed", body);
    }
}
