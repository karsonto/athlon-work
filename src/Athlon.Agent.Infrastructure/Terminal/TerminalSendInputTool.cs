using Athlon.Agent.Core;
using Athlon.Agent.Core.Terminal;

namespace Athlon.Agent.Infrastructure.Terminal;

public sealed class TerminalSendInputTool(ITerminalAutomationHost host) : IAgentTool, ITerminalTool
{
    public ToolDefinition Definition { get; } = new(
        "terminal_send_input",
        "Write text to the workspace Terminal ConPTY session (same interactive shell as the Terminal tab). "
            + "When append_newline is true (default), sends Enter after the text so commands submit. "
            + "Use text=\"\" with append_newline=true to press Enter only.",
        ToolSchema.Object()
            .String("text", "Text to send to the terminal stdin. May be empty when append_newline is true.", required: false)
            .Boolean("append_newline", "Send Enter after text (default true).", required: false)
            .Build(),
        RequiresApproval: true);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        TerminalToolHelper.InvokeHostAsync(async ct =>
        {
            var text = invocation.Arguments.GetString("text") ?? string.Empty;
            var appendNewline = invocation.Arguments.TryGetBoolean("append_newline", out var append)
                ? append
                : true;
            if (string.IsNullOrEmpty(text) && !appendNewline)
            {
                return ToolResult.Failure("Missing input", "Provide text or set append_newline=true to press Enter.");
            }

            await host.SendInputAsync(text, appendNewline, ct).ConfigureAwait(false);
            return ToolResult.Success(
                appendNewline
                    ? (string.IsNullOrEmpty(text) ? "Sent Enter" : "Sent input with Enter")
                    : "Sent input",
                string.IsNullOrEmpty(text) ? "(enter)" : text);
        }, cancellationToken);
}
