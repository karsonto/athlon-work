using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class CommandKillTool(
    BackgroundCommandRegistry backgroundCommands,
    IActiveAgentSessionContext sessionContext) : IAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "command_kill",
        "Stop a background execute_command process and its child processes.",
        ToolSchema.Object()
            .String("command_id", "command_id returned when the command was backgrounded", required: true, minLength: 1)
            .Build(),
        RequiresApproval: false,
        Group: ToolGroup.Builtin);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "command_id", out var commandId, out var error))
        {
            return Task.FromResult(error);
        }

        var sessionId = string.IsNullOrWhiteSpace(sessionContext.SessionId) ? "local" : sessionContext.SessionId;
        var existing = backgroundCommands.TryGet(sessionId, commandId);
        if (existing is null)
        {
            return Task.FromResult(ToolResult.Failure(
                "Command not found",
                $"No background command {commandId} is tracked for this session."));
        }

        if (!existing.Running)
        {
            return Task.FromResult(ToolResult.Success(
                "Command already finished",
                $"command_id: {commandId}\nexit_code: {existing.ExitCode?.ToString() ?? "unknown"}"));
        }

        backgroundCommands.TryKill(sessionId, commandId);
        return Task.FromResult(ToolResult.Success(
            "Command kill requested",
            $"command_id: {commandId}\npid: {existing.Pid}\nThe process tree was asked to stop."));
    }
}
