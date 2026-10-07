using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserScreenshotTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_screenshot",
        "Capture the open Browser tab. By default this is the visible area. Set full_page to true to capture the whole document (very tall pages are clipped at 16384 CSS pixels). Call after navigation or an interaction, before deciding the next step. The result names a screenshot_file to cite in the final report.",
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
