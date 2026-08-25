using System.Net;
using System.Text;
using System.Text.Json;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>构建 WebChatView 外壳 HTML；静态 shell CSS/JS 经 athlon.chat.assets 虚拟主机加载。</summary>
public sealed class ChatHtmlBuilder
{
    public string BuildShellHtml(string? ssoDisplayName = null)
    {
        var assets = ChatMarkdownAssets.VirtualBaseUrl;
        var cache = ChatMarkdownAssets.AssetCacheQuery;
        return "<!DOCTYPE html><html><head>" +
            "<meta charset=\"utf-8\"/>" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\"/>" +
            $"<link rel=\"stylesheet\" href=\"{assets}{ChatMarkdownAssets.GetHighlightStylesheet()}{cache}\"/>" +
            "<style id=\"chat-theme-tokens\">" + GetThemeTokenStyles() + "</style>" +
            "<style id=\"chat-code-syntax\">" + GetCodeSyntaxOverrideStyles() + "</style>" +
            $"<link rel=\"stylesheet\" href=\"{assets}chat-shell.css{cache}\"/>" +
            "</head><body><div id=\"chat-scroll\">" + BuildEmptyStateHtml(ssoDisplayName) +
            "<button id=\"load-older\" type=\"button\" hidden></button>" +
            "<div id=\"messages\"></div></div>" +
            "<div id=\"image-lightbox\" class=\"image-lightbox\" hidden>" +
            "<button type=\"button\" class=\"image-lightbox-backdrop\" aria-label=\"Close\"></button>" +
            "<img class=\"image-lightbox-img\" alt=\"\"/>" +
            "<button type=\"button\" class=\"image-lightbox-close\" aria-label=\"Close\">×</button>" +
            "</div>" +
            $"<script src=\"{assets}highlight.min.js{cache}\"></script>" +
            "<script>" + BuildI18nBootstrapScript() + "</script>" +
            $"<script src=\"{assets}chat-timeline.js{cache}\"></script>" +
            "</body></html>";
    }

    public string BuildDispatchScript(AgentStreamEvent streamEvent) =>
        $"handleEvent({ChatEventSerializer.Serialize(streamEvent)});";

    /// <summary>Updates chat theme tokens in-place so theme switches do not reload the timeline.</summary>
    public string BuildThemeUpdateScript()
    {
        var highlightHref = $"{ChatMarkdownAssets.VirtualBaseUrl}{ChatMarkdownAssets.GetHighlightStylesheet()}{ChatMarkdownAssets.AssetCacheQuery}";
        var tokensB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(GetThemeTokenStyles()));
        var syntaxB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(GetCodeSyntaxOverrideStyles()));
        return
            "applyThemeUpdate(" +
            JsonSerializer.Serialize(highlightHref) +
            ", " +
            JsonSerializer.Serialize(tokensB64) +
            ", " +
            JsonSerializer.Serialize(syntaxB64) +
            ");";
    }

    public string BuildDocumentHtml(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        string? ssoDisplayName = null)
    {
        const string footer = "</body></html>";
        var shell = BuildShellHtml(ssoDisplayName);
        if (!shell.EndsWith(footer, StringComparison.Ordinal))
        {
            return shell;
        }

        var eventsJson = ChatEventSerializer.SerializeEventsToJsonArray(
            ChatEventSerializer.BuildReplayEvents(messages, showToolCalls));
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(eventsJson));
        var replayScript =
            "<script>\n" +
            "(function(){\n" +
            "  try {\n" +
            "    var binary = atob(\"" + payload + "\");\n" +
            "    var bytes = new Uint8Array(binary.length);\n" +
            "    for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);\n" +
            "    replayEvents(JSON.parse(new TextDecoder('utf-8').decode(bytes)));\n" +
            "  } catch (e) {\n" +
            "    console.error(\"replayEvents failed\", e);\n" +
            "  }\n" +
            "})();\n" +
            "</script>\n" +
            footer;

        return shell[..^footer.Length] + replayScript;
    }

    public string BuildI18nUpdateScript()
    {
        var i18nJson = JsonSerializer.Serialize(BuildChatI18n());
        return "window.__chatI18n=" + i18nJson + ";if(typeof applyChatI18n==='function')applyChatI18n();";
    }

    private static string BuildI18nBootstrapScript() =>
        "window.__chatI18n=" + JsonSerializer.Serialize(BuildChatI18n()) + ";";

    private static IReadOnlyDictionary<string, string> BuildChatI18n() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["copy"] = Strings.Get("Chat_Copy"),
            ["copied"] = Strings.Get("Chat_Copied"),
            ["preview"] = Strings.Get("Markdown_PreviewButton"),
            ["code"] = Strings.Get("Chat_Code"),
            ["thinking"] = Strings.Get("Chat_Thinking"),
            ["thought"] = Strings.Get("Chat_Thought"),
            ["seconds"] = Strings.Get("Chat_Seconds"),
            ["welcomeTitle"] = Strings.Get("Chat_WelcomeTitle"),
            ["welcomeTitleWithName"] = Strings.Get("Chat_WelcomeTitleWithName"),
            ["welcomeDescription"] = Strings.Get("Chat_WelcomeDescription"),
            ["loadOlder"] = Strings.Get("RecordGroup_Earlier") + "…",
            ["approvalTitle"] = Strings.Get("Chat_ToolApprovalTitle"),
            ["approvalDescription"] = Strings.Get("Chat_ToolApprovalDescription"),
            ["approvalPending"] = Strings.Get("Chat_ToolApprovalPending"),
            ["approve"] = Strings.Get("Chat_ToolApprovalApprove"),
            ["deny"] = Strings.Get("Chat_ToolApprovalDeny"),
            ["allowedStatus"] = Strings.Get("Chat_ToolApprovalAllowedStatus"),
            ["deniedStatus"] = Strings.Get("Chat_ToolApprovalDeniedStatus"),
            ["approved"] = Strings.Get("Chat_ToolApprovalApproved"),
            ["denied"] = Strings.Get("Chat_ToolApprovalDenied"),
            ["filesChangedOne"] = Strings.Get("Chat_FilesChangedOne"),
            ["filesChangedMany"] = Strings.Get("Chat_FilesChangedMany"),
            ["editedFilesOne"] = Strings.Get("Chat_EditedFilesOne"),
            ["editedFilesMany"] = Strings.Get("Chat_EditedFilesMany"),
            ["exploredFilesOne"] = Strings.Get("Chat_ExploredFilesOne"),
            ["exploredFilesMany"] = Strings.Get("Chat_ExploredFilesMany"),
            ["searchesOne"] = Strings.Get("Chat_SearchesOne"),
            ["searchesMany"] = Strings.Get("Chat_SearchesMany"),
            ["commandsOne"] = Strings.Get("Chat_CommandsOne"),
            ["commandsMany"] = Strings.Get("Chat_CommandsMany"),
            ["thoughtsOne"] = Strings.Get("Chat_ThoughtsOne"),
            ["thoughtsMany"] = Strings.Get("Chat_ThoughtsMany"),
            ["workedFor"] = Strings.Get("Chat_WorkedFor"),
            ["responseDuration"] = Strings.Get("Chat_ResponseDuration"),
            ["said"] = Strings.Get("Chat_ActivityVerbNarration"),
            ["unmodifiedLines"] = Strings.Get("Chat_UnmodifiedLines"),
            ["noDiffAvailable"] = Strings.Get("Chat_NoDiffAvailable"),
        };

    private static string BuildEmptyStateHtml(string? ssoDisplayName)
    {
        // Welcome copy lives in the WPF centered composer hero; keep a hook for JS visibility updates.
        _ = ssoDisplayName;
        return "<div id=\"empty-state\" class=\"empty-state\" aria-hidden=\"true\"></div>";
    }

    private static string GetThemeTokenStyles()
    {
        var isLight = AppThemeManager.CurrentKind == AppThemeKind.Light;
        var chrome = AppThemeManager.Current.Chrome;
        var md = ThemeHtmlStyles.GetMarkdownPalette(assistantTone: true);
        var scrollThumb = AppThemeColor.ToRgba(chrome.ScrollThumb, chrome.ScrollThumbOpacity);

        return $$"""
            :root {
              --chat-bg: {{AppThemeColor.ToHex(chrome.ChatBackgroundTop)}};
              --assistant-text: {{(isLight ? "#1E293B" : "#F4F4F5")}};
              --scroll-thumb: {{scrollThumb}};
              --user-bubble: {{AppThemeColor.ToHex(chrome.UserBubble)}};
              --user-bubble-text: {{AppThemeColor.ToHex(chrome.Text)}};
              --reasoning-border: {{(isLight ? "rgba(221,214,254,0.7)" : "rgba(139,92,246,0.25)")}};
              --reasoning-bg: {{(isLight ? "rgba(245,243,255,0.5)" : "rgba(46,16,101,0.3)")}};
              --reasoning-ring: {{(isLight ? "rgba(237,233,254,0.6)" : "rgba(139,92,246,0.15)")}};
              --reasoning-summary: {{(isLight ? "#4C1D95" : "#EDE9FE")}};
              --reasoning-text: {{(isLight ? "#334155" : "#D4D4D8")}};
              --subtle-text: {{AppThemeColor.ToHex(chrome.SubtleText)}};
              --border: {{AppThemeColor.ToHex(chrome.Border)}};
              --panel: {{AppThemeColor.ToHex(chrome.Panel)}};
              --tool-thinking-bg: {{AppThemeColor.ToHex(chrome.ToolThinkingBg)}};
              --tool-thinking-text: {{AppThemeColor.ToHex(chrome.ToolThinkingText)}};
              --tool-success-bg: {{AppThemeColor.ToHex(chrome.ToolSuccessBg)}};
              --tool-success-text: {{AppThemeColor.ToHex(chrome.ToolSuccessText)}};
              --tool-failure-bg: {{AppThemeColor.ToHex(chrome.ToolFailureBg)}};
              --tool-failure-text: {{AppThemeColor.ToHex(chrome.ToolFailureText)}};
              --diff-add-bg: {{AppThemeColor.ToRgba(chrome.Success, 0.12)}};
              --diff-del-bg: {{AppThemeColor.ToRgba(chrome.Danger, 0.12)}};
              --diff-add-text: {{AppThemeColor.ToHex(chrome.Success)}};
              --diff-del-text: {{AppThemeColor.ToHex(chrome.Danger)}};
              --md-link: {{md.LinkColor}};
              --md-inline-code-bg: {{md.InlineCodeBackground}};
              --md-text: {{md.TextColor}};
              --md-code-block-border: {{md.CodeBlockBorder}};
              --md-code-block-bg: {{md.CodeBlockBackground}};
              --md-code-header: {{md.CodeHeaderColor}};
              --md-code-btn-border: {{md.CodeButtonBorder}};
              --md-code-btn-bg: {{md.CodeButtonBackground}};
              --md-code-btn-color: {{md.CodeButtonColor}};
              --md-code-pre: {{md.CodePreColor}};
              --md-table-border: {{md.TableBorder}};
              --md-table-header-bg: {{md.TableHeaderBackground}};
              --md-blockquote-color: {{md.BlockquoteColor}};
              --md-blockquote-bg: {{md.BlockquoteBackground}};
            }
            """;
    }

    private static string GetCodeSyntaxOverrideStyles()
    {
        if (AppThemeManager.CurrentKind != AppThemeKind.Light)
        {
            return string.Empty;
        }

        return """
            .code-block pre,
            .code-block pre code,
            .code-block pre code.hljs {
              color: #24292F !important;
              background: #F8FAFC !important;
            }
            .code-block .hljs-comment,
            .code-block .hljs-quote {
              color: #57606A !important;
            }
            .code-block .hljs-keyword,
            .code-block .hljs-selector-tag,
            .code-block .hljs-subst {
              color: #CF222E !important;
            }
            .code-block .hljs-string,
            .code-block .hljs-doctag,
            .code-block .hljs-regexp {
              color: #0A3069 !important;
            }
            .code-block .hljs-title,
            .code-block .hljs-section,
            .code-block .hljs-selector-id {
              color: #8250DF !important;
            }
            .code-block .hljs-variable,
            .code-block .hljs-template-variable,
            .code-block .hljs-attribute,
            .code-block .hljs-name {
              color: #953800 !important;
            }
            .code-block .hljs-number,
            .code-block .hljs-literal,
            .code-block .hljs-type,
            .code-block .hljs-built_in,
            .code-block .hljs-builtin-name,
            .code-block .hljs-symbol,
            .code-block .hljs-bullet {
              color: #0550AE !important;
            }
            """;
    }

}
