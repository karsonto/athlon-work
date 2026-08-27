using System.IO;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ChatHtmlBuilderTests
{
    private readonly ChatHtmlBuilder _builder = new();
    private static readonly Lazy<string> TimelineJs = new(() => ReadChatAsset("chat-timeline.js"));
    private static readonly Lazy<string> ShellCss = new(() => ReadChatAsset("chat-shell.css"));

    public ChatHtmlBuilderTests()
    {
        AppCultureManager.SetCulture("zh-CN");
    }

    private string Surface(string? ssoDisplayName = null) =>
        _builder.BuildShellHtml(ssoDisplayName) + "\n" + TimelineJs.Value + "\n" + ShellCss.Value;

    private static string ReadChatAsset(string fileName)
    {
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
    public void BuildShellHtml_without_sso_user_shows_default_welcome_title()
    {
        var surface = Surface();

        Assert.Contains("id=\"chat-scroll\"", surface, StringComparison.Ordinal);
        Assert.Contains("id=\"empty-state\"", surface, StringComparison.Ordinal);
        Assert.Contains("chat-shell.css", surface, StringComparison.Ordinal);
        Assert.Contains("chat-timeline.js", surface, StringComparison.Ordinal);
        Assert.Contains("updateEmptyStateVisibility", surface, StringComparison.Ordinal);
        Assert.Contains("scroller.scrollTop", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("avatar-user", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("Athlon 助手", surface, StringComparison.Ordinal);
        Assert.DoesNotContain(">您<", surface, StringComparison.Ordinal);
        // Welcome copy is rendered by the WPF centered composer hero.
        Assert.DoesNotContain("empty-state-title", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("empty-state-description", surface, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Get("Chat_WelcomeDescription")[..20], surface, StringComparison.Ordinal);
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
        var surface = html + "\n" + TimelineJs.Value;

        Assert.Contains("id=\"empty-state\"", surface, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Format("Chat_WelcomeTitleWithName", "Li Si"), surface, StringComparison.Ordinal);
        Assert.Contains("replayEvents", surface, StringComparison.Ordinal);
        Assert.Contains("updateEmptyStateVisibility", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_includes_theme_token_styles_and_applyThemeUpdate_helper()
    {
        var surface = Surface();

        Assert.Contains("id=\"chat-theme-tokens\"", surface, StringComparison.Ordinal);
        Assert.Contains("id=\"chat-code-syntax\"", surface, StringComparison.Ordinal);
        Assert.Contains("chat-shell.css", surface, StringComparison.Ordinal);
        Assert.Contains("--chat-bg:", surface, StringComparison.Ordinal);
        Assert.Contains("function applyThemeUpdate", surface, StringComparison.Ordinal);
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
        var surface = Surface();

        Assert.Contains("function t(key)", surface, StringComparison.Ordinal);
        Assert.Contains("applyChatI18n", surface, StringComparison.Ordinal);
        Assert.Contains("\"thinking\"", surface, StringComparison.Ordinal);
        Assert.Contains("\"thought\"", surface, StringComparison.Ordinal);
        Assert.Contains("window.__chatI18n", surface, StringComparison.Ordinal);
        Assert.Contains("trackReasoningDuration", surface, StringComparison.Ordinal);
        Assert.Contains("formatReasoningSeconds", surface, StringComparison.Ordinal);
        Assert.Contains("finalizeReasoningLabel", surface, StringComparison.Ordinal);
        Assert.Contains("updateReasoningThinkingLabel", surface, StringComparison.Ordinal);
        Assert.Contains("reasoningFinalizedMs", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("思维链", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_replayEvents_disables_reasoning_duration_tracking()
    {
        var surface = Surface();

        Assert.Contains("state.trackReasoningDuration = false", surface, StringComparison.Ordinal);
        Assert.Contains("state.trackReasoningDuration = true", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_receives_replay_commands_and_batches_dom_updates()
    {
        var surface = Surface();

        Assert.Contains("window.chrome.webview.addEventListener('message'", surface, StringComparison.Ordinal);
        Assert.Contains("command.command === 'replay'", surface, StringComparison.Ordinal);
        Assert.Contains("function beginBatch()", surface, StringComparison.Ordinal);
        Assert.Contains("function endBatch(forceScroll)", surface, StringComparison.Ordinal);
        Assert.Contains("html.replaying .message-row", surface, StringComparison.Ordinal);
        Assert.Contains("endBatch(true)", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_uses_gated_coalesced_scrolling()
    {
        var surface = Surface();

        Assert.Contains("function isNearBottom()", surface, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", surface, StringComparison.Ordinal);
        Assert.Contains("state.autoScrollEnabled", surface, StringComparison.Ordinal);
        Assert.Contains("selectionchange", surface, StringComparison.Ordinal);
        Assert.Contains("hasActiveSelection()", surface, StringComparison.Ordinal);
        Assert.Contains("e.deltaY < 0", surface, StringComparison.Ordinal);
        Assert.Contains("requestToolDetail", surface, StringComparison.Ordinal);
        Assert.Contains("command.command === 'toolDetail'", surface, StringComparison.Ordinal);
        Assert.Contains("turn-activity-tool-detail", surface, StringComparison.Ordinal);
        Assert.Contains("endBatch(false)", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_lazily_highlights_new_code_blocks()
    {
        var surface = Surface();

        Assert.Contains("new IntersectionObserver", surface, StringComparison.Ordinal);
        Assert.Contains("codeObserver.observe(code)", surface, StringComparison.Ordinal);
        Assert.Contains("if (pre.closest('.code-block')) return", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_supports_html_code_block_preview()
    {
        var surface = Surface();

        Assert.Contains("\"preview\":", surface, StringComparison.Ordinal);
        Assert.Contains("t('preview')", surface, StringComparison.Ordinal);
        Assert.Contains("langKey === 'html' || langKey === 'htm'", surface, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'preview', html: raw })", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_supports_loading_older_messages_with_scroll_anchor()
    {
        var surface = Surface();

        Assert.Contains("id=\"load-older\"", surface, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'loadOlder' })", surface, StringComparison.Ordinal);
        Assert.Contains("command.command === 'prepend'", surface, StringComparison.Ordinal);
        Assert.Contains("function prependEvents(events, hasOlderMessages)", surface, StringComparison.Ordinal);
        Assert.Contains("scroller.scrollHeight - previousHeight", surface, StringComparison.Ordinal);
        Assert.Contains("content-visibility: auto", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_supports_inline_tool_approval_actions()
    {
        var surface = Surface();

        Assert.Contains("case 'TOOL_APPROVAL_REQUEST':", surface, StringComparison.Ordinal);
        Assert.Contains("case 'TOOL_APPROVAL_RESOLVED':", surface, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'toolApproval'", surface, StringComparison.Ordinal);
        Assert.Contains("tool-approval-button approve", surface, StringComparison.Ordinal);
        Assert.Contains("ensureToolApprovalPanel", surface, StringComparison.Ordinal);
        Assert.Contains("awaiting_approval", surface, StringComparison.Ordinal);
        Assert.Contains(".tool-approval {", surface, StringComparison.Ordinal);
        Assert.Contains("border-radius: 12px;", surface, StringComparison.Ordinal);
        Assert.Contains("background: var(--panel);", surface, StringComparison.Ordinal);
        Assert.Contains("turn-activity", surface, StringComparison.Ordinal);
        Assert.Contains("scrollTurnActivityThoughts", surface, StringComparison.Ordinal);
        Assert.Contains("case 'TURN_ACTIVITY':", surface, StringComparison.Ordinal);
        Assert.Contains("formatWorkedFor", surface, StringComparison.Ordinal);
        Assert.Contains("syncTurnActivityChevron", surface, StringComparison.Ordinal);
        Assert.Contains("var keepOpen = !!(existing && existing.open);", surface, StringComparison.Ordinal);
        Assert.Contains("details.open = keepOpen;", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("Live: open so the action list is visible while working.", surface, StringComparison.Ordinal);
        Assert.Contains("turn-activity-duration", surface, StringComparison.Ordinal);
        Assert.Contains("turn-activity-line", surface, StringComparison.Ordinal);
        Assert.Contains("\"workedFor\":", surface, StringComparison.Ordinal);
        AssertContainsLocalized(surface, Strings.Get("Chat_WorkedFor"));
        Assert.Contains("files-changed-card", surface, StringComparison.Ordinal);
        Assert.Contains("findFilesChangedTargetCard", surface, StringComparison.Ordinal);
        Assert.Contains("findTurnActivityTargetCard", surface, StringComparison.Ordinal);
        Assert.Contains("case 'FILES_CHANGED':", surface, StringComparison.Ordinal);
        Assert.Contains("user-image-thumb", surface, StringComparison.Ordinal);
        Assert.Contains("image-lightbox", surface, StringComparison.Ordinal);
        Assert.Contains("openImagePreview", surface, StringComparison.Ordinal);
        Assert.Contains(
            "Reasoning is folded into TURN_ACTIVITY; ignore standalone thought bubbles.",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("\"approve\":", surface, StringComparison.Ordinal);
        Assert.Contains("\"deny\":", surface, StringComparison.Ordinal);
        AssertContainsLocalized(surface, Strings.Get("Chat_ToolApprovalApprove"));
        AssertContainsLocalized(surface, Strings.Get("Chat_ToolApprovalDeny"));
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
        var surface = Surface();

        Assert.DoesNotContain("marked.min.js", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("marked.parse", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("finalizeAssistantMarkdown", surface, StringComparison.Ordinal);
        Assert.Contains("case 'TEXT_MESSAGE_END':", surface, StringComparison.Ordinal);
        Assert.Contains("resolveEventHtml(event)", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellHtml_applies_live_assistant_html_without_plain_text_append()
    {
        var surface = Surface();

        Assert.Contains("case 'STATIC_ASSISTANT_HTML':", surface, StringComparison.Ordinal);
        Assert.Contains("event.streaming === true", surface, StringComparison.Ordinal);
        Assert.Contains("streaming !== true", surface, StringComparison.Ordinal);
        Assert.Contains("case 'TEXT_MESSAGE_CONTENT':", surface, StringComparison.Ordinal);
        // Live text display comes from STATIC_ASSISTANT_HTML, not plain textContent deltas.
        Assert.DoesNotContain(
            "case 'TEXT_MESSAGE_CONTENT':\n              finalizeReasoningLabel(event.messageId);\n              if (!state.assistantStarted[event.messageId]) ensureAssistantBubble(event.messageId);\n              appendMessage('assistant'",
            surface,
            StringComparison.Ordinal);
    }
}
