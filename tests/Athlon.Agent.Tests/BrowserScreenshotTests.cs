using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Browser;

namespace Athlon.Agent.Tests;

public sealed class BrowserScreenshotTests
{
    [Fact]
    public async Task BrowserScreenshot_success_returns_attachment_and_short_file_name()
    {
        var image = new ImageAttachment(
            "browser-frame-abc.png",
            "image/png",
            DataUrl: "data:image/png;base64,AQID");
        var host = new FakeBrowserHost
        {
            Capture = new BrowserScreenshotCapture(image, "https://example.com/login", "Login")
        };

        var result = await new BrowserScreenshotTool(host).InvokeAsync(
            new ToolInvocation("browser_screenshot", ToolCallArguments.Empty));

        Assert.True(result.Succeeded);
        Assert.Contains("screenshot_file: browser-frame-abc.png", result.Content, StringComparison.Ordinal);
        Assert.Contains("url: https://example.com/login", result.Content, StringComparison.Ordinal);
        Assert.Contains("title: Login", result.Content, StringComparison.Ordinal);
        var attachment = Assert.Single(result.ImageAttachments!);
        Assert.Equal("browser-frame-abc.png", attachment.FileName);
        Assert.StartsWith(BrowserFrameFiles.Prefix, attachment.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserScreenshot_without_page_fails_and_writes_nothing()
    {
        var host = new FakeBrowserHost();

        var result = await new BrowserScreenshotTool(host).InvokeAsync(
            new ToolInvocation("browser_screenshot", ToolCallArguments.Empty));

        Assert.False(result.Succeeded);
        Assert.Null(result.ImageAttachments);
        Assert.Equal(1, host.CaptureCalls);
        Assert.False(host.WroteFile);
    }

    [Fact]
    public void SaveBrowserFrame_uses_browser_frame_file_name()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ImageAttachmentStore(new TempPaths(root));
            var saved = store.SaveBrowserFrame("session-1", "image/png", [1, 2, 3]);

            Assert.StartsWith(BrowserFrameFiles.Prefix, saved.FileName, StringComparison.Ordinal);
            Assert.EndsWith(".png", saved.FileName, StringComparison.Ordinal);
            Assert.Equal(saved.FileName, Path.GetFileName(saved.LocalPath));
            Assert.True(File.Exists(saved.LocalPath));

            var frame = store.SaveByteFrame("session-1", "desktop.png", "image/png", [4, 5, 6]);
            Assert.Equal("desktop.png", frame.FileName);
            Assert.StartsWith(ImageAttachmentStore.FrameFilePrefix, Path.GetFileName(frame.LocalPath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Pruner_applies_shared_cap_to_browser_frames_and_leaves_uploads()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new TempPaths(root);
            var directory = Path.Combine(paths.SessionsPath, "s1", "attachments");
            Directory.CreateDirectory(directory);
            var oldBrowser = Path.Combine(directory, "browser-frame-old.png");
            var newFrame = Path.Combine(directory, "cu-frame-new.png");
            var upload = Path.Combine(directory, "upload.png");
            File.WriteAllBytes(oldBrowser, [1]);
            File.WriteAllBytes(newFrame, [1]);
            File.WriteAllBytes(upload, [1]);
            File.SetLastWriteTimeUtc(oldBrowser, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(newFrame, DateTime.UtcNow);

            var settings = new AppSettings
            {
                ComputerUse = new()
                {
                    MaxScreenshotsPerSession = 1,
                    ScreenshotRetentionMinutes = 0
                }
            };

            await new ComputerUseAttachmentPruner(paths, settings).PruneAsync("s1");

            Assert.False(File.Exists(oldBrowser));
            Assert.True(File.Exists(newFrame));
            Assert.True(File.Exists(upload));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RetainLatestToolScreenshots_keeps_newest_browser_shots_inside_shared_quota()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "look at the page")
        };
        history.AddRange(Shot("call-cu", "computer_observe", "desktop.png", "data:image/png;base64,CU"));
        history.AddRange(Shot("call-1", "browser_screenshot", "browser-frame-1.png", "data:image/png;base64,OLD"));
        history.Add(ChatMessage.Create(
            MessageRole.User,
            "my upload",
            imageAttachments: [new ImageAttachment("notes.png", "image/png", DataUrl: "data:image/png;base64,USER")]));
        history.AddRange(Shot("call-2", "browser_screenshot", "browser-frame-2.png", "data:image/png;base64,MID"));
        history.AddRange(Shot("call-3", "browser_screenshot", "browser-frame-3.png", "data:image/png;base64,NEW"));

        var messages = ModelMessageBuilder.BuildModelMessages("system", history);
        ModelMessageBuilder.RetainLatestToolScreenshots(messages, maxImages: 2);

        var screenshots = messages.Where(IsToolScreenshot).ToArray();
        Assert.Equal(2, screenshots.Length);
        Assert.All(screenshots, message =>
            Assert.Equal(ModelMessageBuilder.BrowserScreenshotCaption, FirstText(message)));
        var urls = messages.SelectMany(ImageUrls).ToArray();
        Assert.Contains("data:image/png;base64,MID", urls);
        Assert.Contains("data:image/png;base64,NEW", urls);
        Assert.Contains("data:image/png;base64,USER", urls);
        Assert.DoesNotContain("data:image/png;base64,OLD", urls);
        Assert.DoesNotContain("data:image/png;base64,CU", urls);
    }

    [Fact]
    public void ToolCallResult_serialization_includes_screenshot_data_url()
    {
        var image = new ImageAttachment(
            "browser-frame-abc.png",
            "image/png",
            DataUrl: "data:image/png;base64,AQID");
        var call = new AgentToolCall("call-shot", "browser_screenshot", ToolCallArguments.Empty);
        var content = AgentRuntime.FormatToolResult(
            call,
            ToolResult.Success("Captured Browser tab", "screenshot_file: browser-frame-abc.png"));
        var message = new ChatMessageViewModel(ChatMessage.Create(
            MessageRole.Tool,
            content,
            imageAttachments: [image]));

        var json = ChatEventSerializer.SerializeToolResultMarkdown(message);
        Assert.Contains("TOOL_CALL_RESULT", json, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,AQID", json, StringComparison.Ordinal);
        Assert.Contains("browser-frame-abc.png", json, StringComparison.Ordinal);

        var streamJson = ChatEventSerializer.Serialize(
            new AgentStreamEvent.ToolCallResult("call-shot", content, message.MessageId, [image]));
        Assert.Contains("data:image/png;base64,AQID", streamJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Assistant_report_rewrites_known_screenshot_refs_only()
    {
        var known = new ImageAttachment(
            "browser-frame-abc.png",
            "image/png",
            DataUrl: "data:image/png;base64,AQID",
            LocalPath: "/tmp/secret.png");
        var decoy = new ImageAttachment(
            "notes.png",
            "image/png",
            DataUrl: "data:image/png;base64,DECOY",
            LocalPath: "/etc/passwd");
        var assistant = new ChatMessageViewModel(ChatMessage.Create(
            MessageRole.Assistant,
            "![登录页](screenshot:browser-frame-abc.png)\n\n![missing](screenshot:browser-frame-missing.png)\n\n![other](screenshot:notes.png)\n\n![path](screenshot:../../etc/passwd)"));

        var json = ChatEventSerializer.SerializeStaticAssistantHtml(
            assistant,
            browserScreenshots: [known, decoy]);
        using var document = JsonDocument.Parse(json);
        var html = document.RootElement.GetProperty("html").GetString() ?? string.Empty;

        Assert.Contains("data:image/png;base64,AQID", html, StringComparison.Ordinal);
        Assert.Contains("screenshot:browser-frame-missing.png", html, StringComparison.Ordinal);
        Assert.Contains("screenshot:notes.png", html, StringComparison.Ordinal);
        Assert.Contains("screenshot:../../etc/passwd", html, StringComparison.Ordinal);
        Assert.DoesNotContain("DECOY", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/secret.png", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc/passwd", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_shows_tool_card_image_and_report_citation()
    {
        var image = new ImageAttachment(
            "browser-frame-abc.png",
            "image/png",
            DataUrl: "data:image/png;base64,AQID");
        var call = new AgentToolCall("call-shot", "browser_screenshot", ToolCallArguments.Empty);
        var content = AgentRuntime.FormatToolResult(
            call,
            ToolResult.Success("Captured Browser tab", "screenshot_file: browser-frame-abc.png"));
        var messages = new List<ChatMessageViewModel>
        {
            new(ChatMessage.Create(MessageRole.User, "打开登录页")),
            new(ChatMessage.Create(MessageRole.Tool, content, imageAttachments: [image])),
            new(ChatMessage.Create(
                MessageRole.Assistant,
                "![登录页](screenshot:browser-frame-abc.png)"))
        };

        var events = ChatEventSerializer.BuildReplayEvents(messages, showToolCalls: true);
        var toolEvent = Assert.Single(events, json => json.Contains("TOOL_CALL_RESULT", StringComparison.Ordinal));
        Assert.Contains("data:image/png;base64,AQID", toolEvent, StringComparison.Ordinal);
        var assistantEvent = Assert.Single(events, json => json.Contains("STATIC_ASSISTANT_HTML", StringComparison.Ordinal));
        Assert.Contains("data:image/png;base64,AQID", assistantEvent, StringComparison.Ordinal);
    }

    private static IEnumerable<ChatMessage> Shot(string id, string toolName, string fileName, string dataUrl)
    {
        var call = new AgentToolCall(id, toolName, ToolCallArguments.Empty);
        var toolContent = AgentRuntime.FormatToolResult(
            call,
            ToolResult.Success("captured", "screenshot_file: " + fileName));
        yield return ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: [call]);
        yield return ChatMessage.Create(
            MessageRole.Tool,
            toolContent,
            imageAttachments: [new ImageAttachment(fileName, "image/png", DataUrl: dataUrl)]);
    }

    private static bool IsToolScreenshot(AgentModelMessage message)
    {
        var text = FirstText(message);
        return text == ModelMessageBuilder.BrowserScreenshotCaption
            || text == ModelMessageBuilder.ToolScreenshotCaption;
    }

    private static string? FirstText(AgentModelMessage message)
    {
        if (message.Content is not IEnumerable<object> parts)
        {
            return message.Content as string;
        }

        foreach (var part in parts)
        {
            if (part is IDictionary<string, object?> map
                && map.TryGetValue("text", out var text)
                && text is string value)
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<string> ImageUrls(AgentModelMessage message)
    {
        if (message.Content is not IEnumerable<object> parts)
        {
            yield break;
        }

        foreach (var part in parts)
        {
            if (part is IDictionary<string, object?> map
                && map.TryGetValue("image_url", out var imageUrl)
                && imageUrl is IDictionary<string, object?> image
                && image.TryGetValue("url", out var url)
                && url is string value)
            {
                yield return value;
            }
        }
    }

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "athlon-browser-frame-" + Guid.NewGuid().ToString("N"));

    private sealed class TempPaths(string root) : IAppPathProvider
    {
        public string RootPath => root;
        public string ConfigPath => root;
        public string SessionsPath => Path.Combine(root, "sessions");
        public string AuditPath => root;
        public string LogsPath => root;
        public string CredentialsPath => root;
        public string SkillsPath => root;
        public void EnsureCreated() => Directory.CreateDirectory(SessionsPath);
        public string ResolveSkillPath(string path) => path;
    }

    private sealed class FakeBrowserHost : IBrowserAutomationHost
    {
        public BrowserScreenshotCapture? Capture { get; init; }

        public int CaptureCalls { get; private set; }

        public bool WroteFile { get; private set; }

        public Task<BrowserScreenshotCapture?> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            return Task.FromResult(Capture);
        }

        public Task EnsureBrowserTabAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("not used"));

        public Task NavigateAsync(BrowserNavigateAction action, string? url = null, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("not used"));

        public Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<BrowserPageInfo>(new InvalidOperationException("not used"));

        public Task<string> ExecuteAriaAsync(string operation, string? argsJson = null, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new InvalidOperationException("not used"));

        public Task<BrowserNetworkListResult> ListNetworkEntriesAsync(int limit, string? urlContains, CancellationToken cancellationToken = default) =>
            Task.FromException<BrowserNetworkListResult>(new InvalidOperationException("not used"));

        public Task<BrowserNetworkEntryDetail> GetNetworkEntryAsync(string requestId, CancellationToken cancellationToken = default) =>
            Task.FromException<BrowserNetworkEntryDetail>(new InvalidOperationException("not used"));

        public Task<BrowserConsoleReadResult> ReadConsoleAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromException<BrowserConsoleReadResult>(new InvalidOperationException("not used"));

        public Task<IReadOnlyList<BrowserCookieEntry>> GetCookiesAsync(string? url, CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<BrowserCookieEntry>>(new InvalidOperationException("not used"));
    }
}
