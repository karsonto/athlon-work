using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserScreenshotTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_screenshot",
        "Capture the visible area of the open Browser tab. Call after navigation or an interaction, before deciding the next step. The result names a screenshot_file to cite in the final report.",
        ToolSchema.Object().Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var shot = await host.CaptureScreenshotAsync(ct).ConfigureAwait(false);
            if (shot is null)
            {
                return ToolResult.Failure(
                    "Screenshot failed",
                    "No Browser page is open to capture.");
            }

            var content =
                $"screenshot_file: {shot.Image.FileName}\nurl: {shot.Url}\ntitle: {shot.Title}";
            return ToolResult.Success(
                "Captured Browser tab",
                content,
                imageAttachments: [shot.Image]);
        }, cancellationToken);
}
