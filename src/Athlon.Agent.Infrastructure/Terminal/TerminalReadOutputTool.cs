using Athlon.Agent.Core;
using Athlon.Agent.Core.Terminal;

namespace Athlon.Agent.Infrastructure.Terminal;

public sealed class TerminalReadOutputTool(ITerminalAutomationHost host) : IAgentTool, ITerminalTool
{
    public ToolDefinition Definition { get; } = new(
        "terminal_read_output",
        "Read recent output from the workspace Terminal tab ring buffer.",
        ToolSchema.Object()
            .Integer("max_chars", "Maximum characters to return (default 8000).", required: false, minimum: 256, maximum: 64000)
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        TerminalToolHelper.InvokeHostAsync(async ct =>
        {
            var maxChars = invocation.Arguments.TryGetInt32("max_chars", out var parsed) ? parsed : 8000;
            var snapshot = await host.ReadOutputAsync(maxChars, ct).ConfigureAwait(false);
            var summary = snapshot.Truncated
                ? $"Read {snapshot.Text.Length} chars (truncated from {snapshot.TotalChars})"
                : $"Read {snapshot.Text.Length} chars";
            var content = snapshot.Truncated
                ? $"truncated=true\ntotal_chars={snapshot.TotalChars}\n---\n{snapshot.Text}"
                : snapshot.Text;
            return ToolResult.Success(summary, content);
        }, cancellationToken);
}
