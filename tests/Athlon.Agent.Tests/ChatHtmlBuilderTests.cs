using System.Text;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class ChatHtmlBuilderTests
{
    private readonly ChatHtmlBuilder _builder = new();

    public ChatHtmlBuilderTests()
    {
        AppCultureManager.SetCulture("zh-CN");
    }

    [Fact]
    public void BuildShellHtml_without_sso_user_shows_default_welcome_title()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("id=\"chat-scroll\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"empty-state\"", html, StringComparison.Ordinal);
        Assert.Contains(Strings.Get("Chat_WelcomeTitle"), html, StringComparison.Ordinal);
        Assert.Contains(Strings.Get("Chat_WelcomeDescription")[..20], html, StringComparison.Ordinal);
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

        Assert.Contains(Strings.Format("Chat_WelcomeTitleWithName", "Zhang San"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Get("Chat_WelcomeTitle"), html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_encodes_sso_display_name()
    {
        var html = _builder.BuildShellHtml("<script>alert(1)</script>");
        var encodedTitle = System.Net.WebUtility.HtmlEncode(
            Strings.Format("Chat_WelcomeTitleWithName", "<script>alert(1)</script>"));

        Assert.Contains(encodedTitle, html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDocumentHtml_empty_messages_includes_replay_and_visibility_update()
    {
        var html = _builder.BuildDocumentHtml([], showToolCalls: false, ssoDisplayName: "Li Si");

        Assert.Contains(Strings.Format("Chat_WelcomeTitleWithName", "Li Si"), html, StringComparison.Ordinal);
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

    [Fact]
    public void BuildShellHtml_uses_reasoning_state_labels_without_legacy_text()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("function t(key)", html, StringComparison.Ordinal);
        Assert.Contains("applyChatI18n", html, StringComparison.Ordinal);
        Assert.Contains("\"thinking\"", html, StringComparison.Ordinal);
        Assert.Contains("\"thought\"", html, StringComparison.Ordinal);
        Assert.Contains("window.__chatI18n", html, StringComparison.Ordinal);
        Assert.Contains("trackReasoningDuration", html, StringComparison.Ordinal);
        Assert.Contains("formatReasoningSeconds", html, StringComparison.Ordinal);
        Assert.Contains("finalizeReasoningLabel", html, StringComparison.Ordinal);
        Assert.Contains("updateReasoningThinkingLabel", html, StringComparison.Ordinal);
        Assert.Contains("reasoningFinalizedMs", html, StringComparison.Ordinal);
        Assert.DoesNotContain("思维链", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_replayEvents_disables_reasoning_duration_tracking()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("state.trackReasoningDuration = false", html, StringComparison.Ordinal);
        Assert.Contains("state.trackReasoningDuration = true", html, StringComparison.Ordinal);
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
