using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core;

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
        Assert.Contains("updateEmptyStateVisibility", html, StringComparison.Ordinal);
        Assert.Contains("scroller.scrollTop", html, StringComparison.Ordinal);
        Assert.DoesNotContain("avatar-user", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Athlon 助手", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">您<", html, StringComparison.Ordinal);
        // Welcome copy is rendered by the WPF centered composer hero.
        Assert.DoesNotContain("empty-state-title", html, StringComparison.Ordinal);
        Assert.DoesNotContain("empty-state-description", html, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Get("Chat_WelcomeDescription")[..20], html, StringComparison.Ordinal);
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
    public void BuildShellHtml_with_sso_user_keeps_empty_state_hook_without_inline_welcome()
    {
        var html = _builder.BuildShellHtml("Zhang San");

        Assert.Contains("id=\"empty-state\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Format("Chat_WelcomeTitleWithName", "Zhang San"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Get("Chat_WelcomeTitle"), html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_encodes_sso_display_name()
    {
        var html = _builder.BuildShellHtml("<script>alert(1)</script>");

        Assert.Contains("id=\"empty-state\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDocumentHtml_empty_messages_includes_replay_and_visibility_update()
    {
        var html = _builder.BuildDocumentHtml([], showToolCalls: false, ssoDisplayName: "Li Si");

        Assert.Contains("id=\"empty-state\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Format("Chat_WelcomeTitleWithName", "Li Si"), html, StringComparison.Ordinal);
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

    [Fact]
    public void BuildShellHtml_receives_replay_commands_and_batches_dom_updates()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("window.chrome.webview.addEventListener('message'", html, StringComparison.Ordinal);
        Assert.Contains("command.command === 'replay'", html, StringComparison.Ordinal);
        Assert.Contains("function beginBatch()", html, StringComparison.Ordinal);
        Assert.Contains("function endBatch(forceScroll)", html, StringComparison.Ordinal);
        Assert.Contains("html.replaying .message-row", html, StringComparison.Ordinal);
        Assert.Contains("endBatch(true)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_uses_gated_coalesced_scrolling()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("function isNearBottom()", html, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", html, StringComparison.Ordinal);
        Assert.Contains("state.autoScrollEnabled", html, StringComparison.Ordinal);
        Assert.Contains("selectionchange", html, StringComparison.Ordinal);
        Assert.Contains("hasActiveSelection()", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_lazily_highlights_new_code_blocks()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("new IntersectionObserver", html, StringComparison.Ordinal);
        Assert.Contains("codeObserver.observe(code)", html, StringComparison.Ordinal);
        Assert.Contains("if (pre.closest('.code-block')) return", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_supports_html_code_block_preview()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("\"preview\":", html, StringComparison.Ordinal);
        Assert.Contains("t('preview')", html, StringComparison.Ordinal);
        Assert.Contains("langKey === 'html' || langKey === 'htm'", html, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'preview', html: raw })", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_supports_loading_older_messages_with_scroll_anchor()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("id=\"load-older\"", html, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'loadOlder' })", html, StringComparison.Ordinal);
        Assert.Contains("command.command === 'prepend'", html, StringComparison.Ordinal);
        Assert.Contains("function prependEvents(events, hasOlderMessages)", html, StringComparison.Ordinal);
        Assert.Contains("scroller.scrollHeight - previousHeight", html, StringComparison.Ordinal);
        Assert.Contains("content-visibility: auto", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_supports_inline_tool_approval_actions()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("case 'TOOL_APPROVAL_REQUEST':", html, StringComparison.Ordinal);
        Assert.Contains("case 'TOOL_APPROVAL_RESOLVED':", html, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'toolApproval'", html, StringComparison.Ordinal);
        Assert.Contains("tool-approval-button approve", html, StringComparison.Ordinal);
        Assert.Contains("ensureToolApprovalPanel", html, StringComparison.Ordinal);
        Assert.Contains("awaiting_approval", html, StringComparison.Ordinal);
        Assert.Contains(".tool-approval {", html, StringComparison.Ordinal);
        Assert.Contains("border-radius: 12px;", html, StringComparison.Ordinal);
        Assert.Contains("background: var(--panel);", html, StringComparison.Ordinal);
        Assert.Contains("turn-activity", html, StringComparison.Ordinal);
        Assert.Contains("case 'TURN_ACTIVITY':", html, StringComparison.Ordinal);
        Assert.Contains("files-changed-card", html, StringComparison.Ordinal);
        Assert.Contains("case 'FILES_CHANGED':", html, StringComparison.Ordinal);
        Assert.Contains("user-image-thumb", html, StringComparison.Ordinal);
        Assert.Contains("image-lightbox", html, StringComparison.Ordinal);
        Assert.Contains("openImagePreview", html, StringComparison.Ordinal);
        Assert.Contains(
            "Reasoning is folded into TURN_ACTIVITY; ignore standalone thought bubbles.",
            html,
            StringComparison.Ordinal);
        Assert.Contains("\"approve\":", html, StringComparison.Ordinal);
        Assert.Contains("\"deny\":", html, StringComparison.Ordinal);
        AssertContainsLocalized(html, Strings.Get("Chat_ToolApprovalApprove"));
        AssertContainsLocalized(html, Strings.Get("Chat_ToolApprovalDeny"));
    }

    private static void AssertContainsLocalized(string html, string text)
    {
        var encoded = System.Text.Json.JsonSerializer.Serialize(text);
        Assert.True(
            html.Contains(text, StringComparison.Ordinal)
            || html.Contains(encoded.Trim('"'), StringComparison.Ordinal),
            $"Expected localized text '{text}' (or JSON-encoded form) in HTML.");
    }

    [Fact]
    public void BuildShellHtml_does_not_parse_final_markdown_in_javascript()
    {
        var html = _builder.BuildShellHtml();

        Assert.DoesNotContain("marked.min.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("marked.parse", html, StringComparison.Ordinal);
        Assert.DoesNotContain("finalizeAssistantMarkdown", html, StringComparison.Ordinal);
        Assert.Contains("case 'TEXT_MESSAGE_END':", html, StringComparison.Ordinal);
        Assert.Contains("resolveEventHtml(event)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_applies_live_assistant_html_without_plain_text_append()
    {
        var html = _builder.BuildShellHtml();

        Assert.Contains("case 'STATIC_ASSISTANT_HTML':", html, StringComparison.Ordinal);
        Assert.Contains("event.streaming === true", html, StringComparison.Ordinal);
        Assert.Contains("streaming !== true", html, StringComparison.Ordinal);
        Assert.Contains("case 'TEXT_MESSAGE_CONTENT':", html, StringComparison.Ordinal);
        // Live text display comes from STATIC_ASSISTANT_HTML, not plain textContent deltas.
        Assert.DoesNotContain(
            "case 'TEXT_MESSAGE_CONTENT':\n              finalizeReasoningLabel(event.messageId);\n              if (!state.assistantStarted[event.messageId]) ensureAssistantBubble(event.messageId);\n              appendMessage('assistant'",
            html,
            StringComparison.Ordinal);
    }
}
