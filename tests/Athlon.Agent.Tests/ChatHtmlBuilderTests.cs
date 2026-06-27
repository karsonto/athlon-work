using System.Text;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class ChatHtmlBuilderTests
{
    private readonly ChatHtmlBuilder _builder = new();

    [Fact]
    public void BuildShellHtml_without_sso_user_shows_default_welcome_title()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("id=\"chat-scroll\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"empty-state\"", html, StringComparison.Ordinal);
        Assert.Contains("开始新的对话", html, StringComparison.Ordinal);
        Assert.Contains("Athlon Agent 可以帮您分析代码", html, StringComparison.Ordinal);
        Assert.Contains("updateEmptyStateVisibility", html, StringComparison.Ordinal);
        Assert.Contains("scroller.scrollTop", html, StringComparison.Ordinal);
        Assert.DoesNotContain("avatar-user", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Athlon 助手", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">您<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_uses_dark_highlight_stylesheet_by_default()
    {
        AppThemeManager.Apply(AppThemeKind.Dark);

        var html = _builder.BuildShellHtml();

        Assert.Contains("github-dark.min.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("github.min.css", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_uses_light_highlight_stylesheet_in_light_theme()
    {
        AppThemeManager.Apply(AppThemeKind.Light);

        var html = _builder.BuildShellHtml();

        Assert.Contains("github.min.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("github-dark.min.css", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_with_sso_user_shows_personalized_welcome_title()
    {
        var html = _builder.BuildShellHtml("Zhang San");

        Assert.Contains("你好，Zhang San", html, StringComparison.Ordinal);
        Assert.DoesNotContain("开始新的对话", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_encodes_sso_display_name()
    {
        var html = _builder.BuildShellHtml("<script>alert(1)</script>");

        Assert.Contains("你好，&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDocumentHtml_empty_messages_includes_replay_and_visibility_update()
    {
        var html = _builder.BuildDocumentHtml([], showToolCalls: false, ssoDisplayName: "Li Si");

        Assert.Contains("你好，Li Si", html, StringComparison.Ordinal);
        Assert.Contains("replayEvents", html, StringComparison.Ordinal);
        Assert.Contains("updateEmptyStateVisibility", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_includes_theme_token_styles_and_applyThemeUpdate_helper()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("id=\"chat-theme-tokens\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"chat-code-syntax\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"chat-shell-styles\"", html, StringComparison.Ordinal);
        Assert.Contains("--chat-bg:", html, StringComparison.Ordinal);
        Assert.Contains("function applyThemeUpdate", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildThemeUpdateScript_updates_tokens_not_full_stylesheet()
    {
        AppThemeManager.Apply(AppThemeKind.Light);

        var script = _builder.BuildThemeUpdateScript();

        Assert.Contains("applyThemeUpdate(", script, StringComparison.Ordinal);
        Assert.Contains("github.min.css", script, StringComparison.Ordinal);
        Assert.DoesNotContain("github-dark.min.css", script, StringComparison.Ordinal);
    }
}

public sealed class SignedInUserSectionTests
{
    [Fact]
    public void Append_skips_when_user_name_missing()
    {
        var section = new SignedInUserSection();
        var builder = new StringBuilder();
        var context = CreateContext(ssoUserDisplayName: null);

        section.Append(builder, context);

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_includes_signed_in_user_when_name_present()
    {
        var section = new SignedInUserSection();
        var builder = new StringBuilder();
        var context = CreateContext(ssoUserDisplayName: "Zhang San");

        section.Append(builder, context);

        var text = builder.ToString();
        Assert.Contains("The signed-in user is Zhang San.", text, StringComparison.Ordinal);
        Assert.Contains("Address them by name when appropriate.", text, StringComparison.Ordinal);
    }

    private static EnvironmentPromptContext CreateContext(string? ssoUserDisplayName) =>
        new()
        {
            Session = AgentSession.Create("test"),
            Tools = Array.Empty<ToolDefinition>(),
            SkillsDirectory = @"C:\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\skills", @"C:\app"),
            PromptSettings = new PromptSettings(),
            SsoUserDisplayName = ssoUserDisplayName
        };
}
