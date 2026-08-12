using Athlon.Agent.Core;
using Athlon.Agent.Core.Terminal;

namespace Athlon.Agent.Infrastructure.Terminal;

public sealed class TerminalOpenTool(ITerminalAutomationHost host) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "terminal_open",
        "Open or activate the workspace Terminal tab. Optionally pass command to start an interactive CLI "
            + "(for example claude). After success, terminal_send_input and terminal_read_output unlock on the next model step.",
        ToolSchema.Object()
            .String(
                "command",
                "Optional command to run after the terminal tab is ready (sent with a trailing newline).",
                required: false)
            .Build(),
        RequiresApproval: true);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        TerminalToolHelper.InvokeHostAsync(async ct =>
        {
            await host.EnsureTerminalTabAsync(ct).ConfigureAwait(false);

            var command = invocation.Arguments.GetString("command")?.Trim();
            if (!string.IsNullOrWhiteSpace(command))
            {
                await host.SendInputAsync(command, appendNewline: true, ct).ConfigureAwait(false);
            }

            var info = await host.GetSessionInfoAsync(ct).ConfigureAwait(false);
            var summary = string.IsNullOrWhiteSpace(command)
                ? "Terminal tab is ready"
                : $"Terminal tab is ready; sent command: {command}";
            var content =
                $"title={info.Title}\ncwd={info.WorkingDirectory ?? "(none)"}\nattached={info.IsAttached}\n" +
                "terminal_send_input and terminal_read_output are now available for the next model step.";
            return ToolResult.Success(summary, content);
        }, cancellationToken);
}
