using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserScreenshotTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_screenshot",
        ToolDescriptions.BrowserScreenshot,
        ToolSchema.Object()
            .Boolean(
                "full_page",
                "When true, capture the whole document instead of the visible area.",
                defaultValue: false)
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var fullPage = invocation.Arguments.GetBoolean("full_page");
            var shot = await host.CaptureScreenshotAsync(fullPage, ct).ConfigureAwait(false);
            if (shot is null)
            {
                return ToolResult.Failure(
                    "Screenshot failed",
                    "No Browser page is open to capture.");
            }

            var capture = shot.FullPage ? "full_page" : "viewport";
            var content =
                $"screenshot_file: {shot.Image.FileName}\nurl: {shot.Url}\ntitle: {shot.Title}\ncapture: {capture}";
            if (shot.Clipped)
            {
                content += "\nclipped: true";
            }
            return ToolResult.Success(
                "Captured Browser tab",
                content,
                imageAttachments: [shot.Image]);
        }, cancellationToken);
}
