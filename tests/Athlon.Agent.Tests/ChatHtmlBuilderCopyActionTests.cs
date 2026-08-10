using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class ChatHtmlBuilderCopyActionTests
{
    [Fact]
    public void BuildShellHtml_IncludesMessageCopyActionMarkupAndScript()
    {
        var html = new ChatHtmlBuilder().BuildShellHtml();

        Assert.Contains("message-stack", html);
        Assert.Contains("message-actions", html);
        Assert.Contains("message-action-btn", html);
        Assert.Contains("dataset.copyText", html);
        Assert.Contains("createMessageActions", html);
        Assert.Contains("copyMessageText", html);
        Assert.Contains("resolveRowCopyText", html);
    }

    [Fact]
    public void BuildShellHtml_IncludesCopyI18nKeys()
    {
        var html = new ChatHtmlBuilder().BuildShellHtml();

        Assert.Contains("\"copy\"", html);
        Assert.Contains("\"copied\"", html);
    }
}
