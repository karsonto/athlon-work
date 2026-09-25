using System.IO;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class ChatHtmlBuilderCopyActionTests
{
    private static readonly string[] TimelineModules =
    [
        "timeline-state.js",
        "timeline-render.js",
        "timeline-cards.js",
        "timeline-protocol.js"
    ];

    private static string TimelineJs => ReadChatAsset("chat-timeline.js");

    private static string ReadChatAsset(string fileName)
    {
        if (string.Equals(fileName, "chat-timeline.js", StringComparison.Ordinal))
        {
            var combined = new System.Text.StringBuilder();
            foreach (var module in TimelineModules)
            {
                combined.Append(ReadChatAsset(module));
            }

            return combined.ToString();
        }

        var fromBase = Path.Combine(AppContext.BaseDirectory, "Assets", "Chat", fileName);
        if (File.Exists(fromBase))
            return File.ReadAllText(fromBase);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Athlon.Agent.App", "Assets", "Chat", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Chat asset not found: {fileName}");
    }

    [Fact]
    public void BuildShellHtml_IncludesMessageCopyActionMarkupAndScript()
    {
        var surface = new ChatHtmlBuilder().BuildShellHtml() + "\n" + TimelineJs + "\n" + ReadChatAsset("chat-shell.css");

        Assert.Contains("message-stack", surface);
        Assert.Contains("message-actions", surface);
        Assert.Contains("message-action-btn", surface);
        Assert.Contains("dataset.copyText", surface);
        Assert.Contains("createMessageActions", surface);
        Assert.Contains("copyMessageText", surface);
        Assert.Contains("resolveRowCopyText", surface);
    }

    [Fact]
    public void BuildShellHtml_IncludesCopyI18nKeys()
    {
        var html = new ChatHtmlBuilder().BuildShellHtml();

        Assert.Contains("\"copy\"", html);
        Assert.Contains("\"copied\"", html);
    }

    [Fact]
    public void BuildShellHtml_IncludesReadAloudButtonMarkupAndScript()
    {
        var surface = new ChatHtmlBuilder().BuildShellHtml() + "\n" + TimelineJs + "\n" + ReadChatAsset("chat-shell.css");

        Assert.Contains("createPlayButton", surface);
        Assert.Contains("applyTtsState", surface);
        Assert.Contains("applyChatTtsConfig", surface);
        Assert.Contains("toSpeakableText", surface);
        Assert.Contains("message-action-btn.playing", surface);
    }

    [Fact]
    public void BuildShellHtml_IncludesReadAloudI18nKeys()
    {
        var html = new ChatHtmlBuilder().BuildShellHtml();

        Assert.Contains("\"play\"", html);
        Assert.Contains("\"stop\"", html);
        Assert.Contains("\"playing\"", html);
        Assert.Contains("\"ttsFailed\"", html);
    }

    [Fact]
    public void BuildTtsConfigScript_publishes_only_the_enabled_flag()
    {
        // The API key and endpoint must never reach the page: synthesis runs in the desktop process.
        var script = ChatHtmlBuilder.BuildTtsConfigScript(true);

        Assert.Contains("\"enabled\":true", script);
        Assert.Contains("applyChatTtsConfig", script);
        Assert.DoesNotContain("endpoint", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("voice", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTtsConfigScript_reports_disabled_state()
    {
        var script = ChatHtmlBuilder.BuildTtsConfigScript(false);

        Assert.Contains("\"enabled\":false", script);
    }
}
