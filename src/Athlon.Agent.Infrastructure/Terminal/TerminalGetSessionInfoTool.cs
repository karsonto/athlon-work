using Athlon.Agent.Core;
using Athlon.Agent.Core.Terminal;

namespace Athlon.Agent.Infrastructure.Terminal;

public sealed class TerminalGetSessionInfoTool(ITerminalAutomationHost host) : IAgentTool, ITerminalTool
{
    public ToolDefinition Definition { get; } = new(
        "terminal_get_session_info",
        "Get metadata for the active workspace Terminal tab (title, cwd, attachment state).",
        ToolSchema.Object().Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        TerminalToolHelper.InvokeHostAsync(async ct =>
        {
            var info = await host.GetSessionInfoAsync(ct).ConfigureAwait(false);
            var summary = string.IsNullOrWhiteSpace(info.Title)
                ? "Terminal session"
                : info.Title;
            var content =
                $"title={info.Title}\ncwd={info.WorkingDirectory ?? "(none)"}\n" +
                $"attached={info.IsAttached}\nprocess_alive={info.ProcessAlive}";
            return ToolResult.Success(summary, content);
        }, cancellationToken);
}
