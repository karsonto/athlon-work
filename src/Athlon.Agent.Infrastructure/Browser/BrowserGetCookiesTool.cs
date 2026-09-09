using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserGetCookiesTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_get_cookies",
        "Read the cookies the Browser tab stores for a site (name, value, domain, path, httpOnly, secure, sameSite, expiry). "
            + "Omit url to use the currently open page; otherwise pass an http(s) URL or host name.",
        ToolSchema.Object()
            .String("url", "Optional http(s) URL or host name. Defaults to the current page URL.")
            .Build(),
        RequiresApproval: true);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var url = invocation.Arguments.GetString("url");
            var cookies = await host.GetCookiesAsync(
                string.IsNullOrWhiteSpace(url) ? null : url.Trim(), ct).ConfigureAwait(false);
            var content = JsonElementFormatter.SerializeForDisplay(cookies, indented: true);
            return ToolResult.Success(
                $"{cookies.Count} cookie(s)", content);
        }, cancellationToken);
}
