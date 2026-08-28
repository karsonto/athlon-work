using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserNetworkListTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_network_list",
        "List recent network requests captured from the Browser tab (newest last). "
            + "Use browser_network_get with a requestId for full headers and bodies.",
        ToolSchema.Object()
            .Integer("limit", "Max entries to return (1-50, default 50).")
            .String("urlContains", "Optional URL substring filter.")
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var limit = invocation.Arguments.TryGetInt32("limit", out var parsed)
                ? Math.Clamp(parsed, 1, 50)
                : 50;
            var urlContains = invocation.Arguments.GetString("urlContains");
            var result = await host.ListNetworkEntriesAsync(limit, urlContains, ct).ConfigureAwait(false);
            var content = JsonElementFormatter.SerializeForDisplay(result, indented: true);
            return ToolResult.Success(
                $"{result.Entries.Count} network request(s) (buffered {result.TotalBuffered})",
                content);
        }, cancellationToken);
}

public sealed class BrowserNetworkGetTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_network_get",
        "Get full details for one captured network request: headers, request body, and response body.",
        ToolSchema.Object()
            .String("requestId", "Request id from browser_network_list.", required: true, minLength: 1)
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var requestId = invocation.Arguments.GetString("requestId")?.Trim();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return ToolResult.Failure("Missing requestId", "requestId is required.");
            }

            var detail = await host.GetNetworkEntryAsync(requestId, ct).ConfigureAwait(false);
            var content = JsonElementFormatter.SerializeForDisplay(detail, indented: true);
            return ToolResult.Success(
                $"{detail.Summary.Method} {detail.Summary.Url}",
                content);
        }, cancellationToken);
}

public sealed class BrowserConsoleReadTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_console_read",
        "Read recent console logs and uncaught JavaScript exceptions from the Browser tab.",
        ToolSchema.Object()
            .Integer("limit", "Max entries to return (1-100, default 100).")
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var limit = invocation.Arguments.TryGetInt32("limit", out var parsed)
                ? Math.Clamp(parsed, 1, 100)
                : 100;
            var result = await host.ReadConsoleAsync(limit, ct).ConfigureAwait(false);
            var content = JsonElementFormatter.SerializeForDisplay(result, indented: true);
            return ToolResult.Success(
                $"{result.Entries.Count} console entry(ies) (buffered {result.TotalBuffered})",
                content);
        }, cancellationToken);
}
