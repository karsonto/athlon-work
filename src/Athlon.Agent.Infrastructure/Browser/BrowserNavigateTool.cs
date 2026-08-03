using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserNavigateTool(IBrowserAutomationHost host) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_navigate",
        "Navigate the Browser workspace tab. Pass url to open a page (creates a Browser tab if none exists). "
            + "Special url values: \"back\", \"forward\", \"reload\".",
        ToolSchema.Object()
            .String(
                "url",
                "Absolute or host URL (https://…), or back/forward/reload.",
                required: true,
                minLength: 1)
            .Build(),
        RequiresApproval: true);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var url = invocation.Arguments.GetString("url")?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                return ToolResult.Failure("Missing url", "url is required.");
            }

            BrowserNavigateAction action;
            string? navigateUrl = null;
            if (string.Equals(url, "back", StringComparison.OrdinalIgnoreCase))
            {
                action = BrowserNavigateAction.Back;
            }
            else if (string.Equals(url, "forward", StringComparison.OrdinalIgnoreCase))
            {
                action = BrowserNavigateAction.Forward;
            }
            else if (string.Equals(url, "reload", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(url, "refresh", StringComparison.OrdinalIgnoreCase))
            {
                action = BrowserNavigateAction.Reload;
            }
            else
            {
                action = BrowserNavigateAction.Url;
                navigateUrl = url;
            }

            await host.NavigateAsync(action, navigateUrl, ct).ConfigureAwait(false);
            var info = await host.GetPageInfoAsync(ct).ConfigureAwait(false);
            var summary = action == BrowserNavigateAction.Url
                ? $"Navigated to {info.Url}"
                : $"Browser {action.ToString().ToLowerInvariant()}";
            var content =
                $"url={info.Url}\ntitle={info.Title}\n" +
                "ARIA page tools (browser_read_aria_tree, browser_find_aria_nodes, …) are now available for the next model step.";
            return ToolResult.Success(summary, content);
        }, cancellationToken);
}
