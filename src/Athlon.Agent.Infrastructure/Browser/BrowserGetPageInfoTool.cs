using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserGetPageInfoTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_get_page_info",
        "Get the current Browser tab URL and document title.",
        ToolSchema.Object().Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var info = await host.GetPageInfoAsync(ct).ConfigureAwait(false);
            return ToolResult.Success(
                string.IsNullOrWhiteSpace(info.Title) ? info.Url : $"{info.Title} — {info.Url}",
                $"url={info.Url}\ntitle={info.Title}");
        }, cancellationToken);
}
