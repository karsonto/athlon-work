using System.Net;
using System.Text;
using System.Text.Json;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>构建 WebChatView 外壳 HTML 与 AG-UI 风格的事件驱动时间线脚本。</summary>
public sealed class ChatHtmlBuilder
{
    public string BuildShellHtml(string? ssoDisplayName = null)
    {
        var assets = ChatMarkdownAssets.VirtualBaseUrl;
        return "<!DOCTYPE html><html><head>" +
            "<meta charset=\"utf-8\"/>" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\"/>" +
            $"<link rel=\"stylesheet\" href=\"{assets}{ChatMarkdownAssets.GetHighlightStylesheet()}\"/>" +
            "<style id=\"chat-theme-tokens\">" + GetThemeTokenStyles() + "</style>" +
            "<style id=\"chat-code-syntax\">" + GetCodeSyntaxOverrideStyles() + "</style>" +
            "<style id=\"chat-shell-styles\">" + GetStaticShellStyles() + "</style>" +
            "</head><body><div id=\"chat-scroll\">" + BuildEmptyStateHtml(ssoDisplayName) +
            "<button id=\"load-older\" type=\"button\" hidden></button>" +
            "<div id=\"messages\"></div></div>" +
            "<div id=\"image-lightbox\" class=\"image-lightbox\" hidden>" +
            "<button type=\"button\" class=\"image-lightbox-backdrop\" aria-label=\"Close\"></button>" +
            "<img class=\"image-lightbox-img\" alt=\"\"/>" +
            "<button type=\"button\" class=\"image-lightbox-close\" aria-label=\"Close\">×</button>" +
            "</div>" +
            $"<script src=\"{assets}highlight.min.js\"></script>" +
            "<script>" + BuildI18nBootstrapScript() + "</script>" +
            "<script>" + GetTimelineScript() + "</script>" +
            "</body></html>";
    }

    public string BuildDispatchScript(AgentStreamEvent streamEvent) =>
        $"handleEvent({ChatEventSerializer.Serialize(streamEvent)});";

    /// <summary>Updates chat theme tokens in-place so theme switches do not reload the timeline.</summary>
    public string BuildThemeUpdateScript()
    {
        var highlightHref = $"{ChatMarkdownAssets.VirtualBaseUrl}{ChatMarkdownAssets.GetHighlightStylesheet()}";
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

    private static string GetStaticShellStyles() =>
        """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        html, body {
          font-family: "Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", "PingFang SC", "Hiragino Sans GB", system-ui, sans-serif;
          font-size: 14px;
          font-weight: 400;
          line-height: 1.5;
          letter-spacing: normal;
          color: var(--assistant-text);
          background: var(--chat-bg);
          height: 100%;
          overflow: hidden;
          /* Keep ClearType/subpixel AA on Windows; antialiased makes Chromium text soft/blurry. */
          -webkit-font-smoothing: auto;
          text-rendering: auto;
        }
        #chat-scroll {
          position: relative;
          height: 100%;
          overflow-x: hidden;
          overflow-y: auto;
          padding: 24px 20px 24px 24px;
          background: var(--chat-bg);
        }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: transparent; }
        ::-webkit-scrollbar-thumb {
          border: 2px solid transparent;
          border-radius: 9999px;
          background: var(--scroll-thumb);
          background-clip: padding-box;
        }
        #messages {
          display: flex;
          flex-direction: column;
          gap: 20px;
          max-width: 100%;
        }
        #load-older {
          display: block;
          margin: 0 auto 16px;
          border: 1px solid var(--border);
          border-radius: 8px;
          padding: 6px 12px;
          background: var(--panel);
          color: var(--subtle-text);
          cursor: pointer;
        }
        #load-older[hidden] { display: none; }
        .message-row {
          content-visibility: auto;
          contain-intrinsic-size: auto 96px;
        }
        .empty-state {
          display: none !important;
        }
        @keyframes fadeIn {
          from { opacity: 0; transform: translateY(8px); }
          to { opacity: 1; transform: translateY(0); }
        }
        .message-row {
          display: flex;
          align-items: flex-start;
          animation: fadeIn 0.25s ease;
        }
        html.replaying .message-row { animation: none; }
        .message-row.user { justify-content: flex-end; }
        .message-row.assistant { justify-content: flex-start; }
        .bubble {
          max-width: 85%;
        }
        .message-row.user .bubble {
          background: var(--user-bubble);
          color: var(--user-bubble-text);
          border-radius: 20px;
          padding: 12px 16px;
          box-shadow: none;
        }
        .message-row.assistant .bubble {
          background: transparent;
          color: var(--assistant-text);
          padding: 0;
          box-shadow: none;
        }
        .user-text {
          white-space: pre-wrap;
          word-break: break-word;
          font-size: 14px;
          line-height: 1.75;
          color: var(--user-bubble-text);
        }
        .user-images {
          display: flex;
          flex-wrap: wrap;
          gap: 8px;
          margin-bottom: 8px;
        }
        .user-images:last-child { margin-bottom: 0; }
        .user-image-thumb {
          width: 96px;
          height: 96px;
          object-fit: cover;
          border-radius: 10px;
          cursor: zoom-in;
          border: 1px solid var(--border);
          background: var(--chat-bg);
          display: block;
        }
        .image-lightbox {
          position: fixed;
          inset: 0;
          z-index: 10000;
          display: flex;
          align-items: center;
          justify-content: center;
        }
        .image-lightbox[hidden] { display: none !important; }
        .image-lightbox-backdrop {
          position: absolute;
          inset: 0;
          border: 0;
          padding: 0;
          margin: 0;
          background: rgba(0, 0, 0, 0.72);
          cursor: zoom-out;
        }
        .image-lightbox-img {
          position: relative;
          z-index: 1;
          max-width: min(92vw, 1200px);
          max-height: 90vh;
          border-radius: 10px;
          box-shadow: 0 12px 40px rgba(0, 0, 0, 0.35);
          object-fit: contain;
          background: #111;
        }
        .image-lightbox-close {
          position: absolute;
          top: 16px;
          right: 18px;
          z-index: 2;
          width: 36px;
          height: 36px;
          border: 0;
          border-radius: 999px;
          background: rgba(255, 255, 255, 0.16);
          color: #fff;
          font-size: 22px;
          line-height: 1;
          cursor: pointer;
        }
        .image-lightbox-close:hover { background: rgba(255, 255, 255, 0.28); }
        .reasoning-block {
          max-width: 85%;
          overflow: hidden;
          border-radius: 16px;
          border: 1px solid var(--reasoning-border);
          background: var(--reasoning-bg);
          box-shadow: 0 0 0 1px var(--reasoning-ring);
        }
        .reasoning-block > summary {
          display: flex;
          align-items: center;
          gap: 8px;
          padding: 10px 12px;
          cursor: pointer;
          list-style: none;
          font-size: 12px;
          font-weight: 500;
          color: var(--reasoning-summary);
          user-select: none;
        }
        .reasoning-block > summary::-webkit-details-marker { display: none; }
        .reasoning-chevron {
          display: inline-block;
          font-size: 14px;
          transition: transform 0.15s ease;
        }
        .reasoning-block[open] .reasoning-chevron { transform: rotate(90deg); }
        .reasoning-content {
          border-top: 1px solid var(--reasoning-border);
          padding: 10px 12px;
          max-height: 288px;
          overflow-y: auto;
          white-space: pre-wrap;
          word-break: break-word;
          font-size: 12px;
          line-height: 1.6;
          color: var(--reasoning-text);
        }
        .message-content {
          white-space: pre-wrap;
          word-break: break-word;
          overflow-wrap: anywhere;
        }
        .message-content.md-root,
        .tool-result-html.md-root {
          white-space: normal;
        }
        .message-content.md-root p,
        .tool-result-html.md-root p { margin: 0 0 12px; }
        .message-content.md-root p:last-child,
        .tool-result-html.md-root p:last-child { margin-bottom: 0; }
        .message-content.md-root ul,
        .message-content.md-root ol,
        .tool-result-html.md-root ul,
        .tool-result-html.md-root ol {
          margin: 0 0 12px;
          padding-left: 24px;
        }
        .message-content.md-root li,
        .tool-result-html.md-root li { margin-bottom: 6px; }
        .message-content.md-root h1,
        .message-content.md-root h2,
        .message-content.md-root h3,
        .message-content.md-root h4,
        .tool-result-html.md-root h1,
        .tool-result-html.md-root h2,
        .tool-result-html.md-root h3,
        .tool-result-html.md-root h4 {
          margin: 16px 0 10px;
          font-weight: 600;
          line-height: 1.35;
        }
        .message-content.md-root h1 { font-size: 1.5em; }
        .message-content.md-root h2 { font-size: 1.3em; }
        .message-content.md-root h3 { font-size: 1.15em; }
        .message-content.md-root a,
        .tool-result-html.md-root a {
          color: var(--md-link);
          text-decoration: underline;
          text-underline-offset: 2px;
        }
        .message-content.md-root code:not(pre code),
        .tool-result-html.md-root code:not(pre code) {
          border-radius: 6px;
          background: var(--md-inline-code-bg);
          padding: 2px 6px;
          font-family: Consolas, "Cascadia Code", monospace;
          font-size: 0.9em;
          color: var(--md-text);
        }
        .message-content.md-root pre,
        .tool-result-html.md-root pre,
        .tool-pre {
          margin: 0;
          padding: 0;
          border: none;
          border-radius: 0;
          background: transparent;
          overflow: visible;
          white-space: pre;
        }
        .code-block {
          margin: 16px 0;
          border: 1px solid var(--md-code-block-border);
          border-radius: 16px;
          overflow: hidden;
          background: var(--md-code-block-bg);
        }
        .code-block-header {
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 8px;
          padding: 8px 16px;
          border-bottom: 1px solid var(--md-code-block-border);
          font-size: 12px;
          color: var(--md-code-header);
        }
        .code-block-actions { display: flex; gap: 8px; }
        .code-btn {
          border: 1px solid var(--md-code-btn-border);
          border-radius: 6px;
          background: var(--md-code-btn-bg);
          color: var(--md-code-btn-color);
          padding: 4px 8px;
          font-size: 12px;
          cursor: pointer;
        }
        .code-btn:hover {
          background: var(--md-code-block-bg);
        }
        .code-btn.copied {
          border-color: rgba(16, 185, 129, 0.6);
          background: rgba(16, 185, 129, 0.1);
          color: #6EE7B7;
        }
        .code-block pre {
          margin: 0;
          padding: 16px;
          overflow-x: auto;
          font-size: 13px;
          line-height: 1.5;
          color: var(--md-code-pre);
          background: var(--md-code-block-bg);
        }
        .code-block pre code,
        .code-block pre code.hljs {
          font-family: Consolas, "Cascadia Code", monospace;
          white-space: pre;
          background: transparent;
          padding: 0;
        }
        .message-content.md-root table,
        .tool-result-html.md-root table {
          width: 100%;
          border-collapse: collapse;
          margin: 16px 0;
          font-size: 13px;
        }
        .message-content.md-root .table-wrap,
        .tool-result-html.md-root .table-wrap {
          margin: 16px 0;
          overflow-x: auto;
          border-radius: 12px;
          border: 1px solid var(--md-table-border);
        }
        .message-content.md-root th,
        .tool-result-html.md-root th {
          background: var(--md-table-header-bg);
          padding: 8px 12px;
          text-align: left;
          font-weight: 600;
          border-bottom: 1px solid var(--md-table-border);
        }
        .message-content.md-root td,
        .tool-result-html.md-root td {
          padding: 8px 12px;
          border-top: 1px solid var(--md-table-border);
          vertical-align: top;
        }
        .message-content.md-root blockquote,
        .tool-result-html.md-root blockquote {
          margin: 12px 0;
          padding: 8px 14px;
          border-left: 3px solid #3b82f6;
          color: var(--md-blockquote-color);
          background: var(--md-blockquote-bg);
          border-radius: 0 8px 8px 0;
        }
        .message.tool {
          max-width: 85%;
          border-radius: 16px;
          overflow: hidden;
          border: 1px solid var(--border);
          background: var(--panel);
          box-shadow: 0 1px 2px rgba(15,23,42,0.06);
        }
        .message.tool > summary {
          display: flex;
          align-items: center;
          flex-wrap: wrap;
          gap: 8px;
          padding: 10px 14px;
          cursor: pointer;
          list-style: none;
          background: var(--panel);
          user-select: none;
          font-size: 13px;
          font-weight: 500;
          color: var(--assistant-text);
        }
        .message.tool > summary::-webkit-details-marker { display: none; }
        .message.tool > summary::before {
          content: "›";
          font-size: 14px;
          color: var(--subtle-text);
          margin-right: 4px;
          transition: transform 0.15s ease;
        }
        .message.tool[open] > summary::before { transform: rotate(90deg); }
        .tool-status {
          font-size: 11px;
          font-weight: 600;
          padding: 2px 8px;
          border-radius: 999px;
          margin-left: auto;
        }
        .tool-status.running { background: var(--tool-thinking-bg); color: var(--tool-thinking-text); }
        .tool-status.success { background: var(--tool-success-bg); color: var(--tool-success-text); }
        .tool-status.failed { background: var(--tool-failure-bg); color: var(--tool-failure-text); }
        .tool-status.cancelled { background: var(--panel); color: var(--subtle-text); }
        .files-changed-card {
          max-width: 92%;
          margin: 8px 0;
          padding: 10px 12px;
          border: 1px solid var(--border);
          border-radius: 12px;
          background: var(--panel);
          color: var(--assistant-text);
        }
        .files-changed-title {
          font-size: 13px;
          font-weight: 600;
          margin-bottom: 6px;
          color: var(--assistant-text);
        }
        .files-changed-list { display: flex; flex-direction: column; gap: 2px; }
        .files-changed-item { border-radius: 8px; }
        .files-changed-item.open { background: rgba(127,127,127,0.08); }
        .files-changed-row {
          display: flex;
          align-items: center;
          gap: 8px;
          width: 100%;
          padding: 6px 8px;
          border: 0;
          background: transparent;
          color: inherit;
          font: inherit;
          cursor: pointer;
          text-align: left;
          border-radius: 8px;
        }
        .files-changed-row:hover { background: rgba(127,127,127,0.12); }
        .files-changed-name {
          flex: 1;
          min-width: 0;
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
          font-size: 12px;
        }
        .files-changed-counts {
          display: inline-flex;
          gap: 6px;
          font-size: 12px;
          font-weight: 600;
          font-variant-numeric: tabular-nums;
          flex-shrink: 0;
        }
        .files-changed-diff {
          display: none;
          margin: 0 8px 8px;
          max-height: 280px;
          overflow: auto;
          border: 1px solid var(--border);
          border-radius: 8px;
          background: var(--chat-bg);
        }
        .files-changed-item.open .files-changed-diff { display: block; }
        .turn-activity {
          max-width: 92%;
          margin: 2px 0 6px;
          color: var(--subtle-text);
          font-size: 12px;
          line-height: 1.5;
        }
        .turn-activity > summary {
          list-style: none;
          cursor: pointer;
          display: flex;
          align-items: baseline;
          flex-wrap: wrap;
          gap: 6px;
          user-select: none;
          padding: 2px 0;
        }
        .turn-activity > summary::-webkit-details-marker { display: none; }
        .turn-activity-summary-text {
          color: var(--subtle-text);
        }
        .turn-activity-counts {
          display: inline-flex;
          gap: 6px;
          font-weight: 600;
          font-variant-numeric: tabular-nums;
        }
        .turn-activity-add { color: var(--diff-add-text); }
        .turn-activity-del { color: var(--diff-del-text); }
        .turn-activity-chevron {
          display: inline-block;
          margin-left: 2px;
          transition: transform 0.15s ease;
          opacity: 0.7;
        }
        .turn-activity[open] .turn-activity-chevron { transform: rotate(90deg); }
        .turn-activity-body {
          margin: 4px 0 2px 2px;
          padding-left: 10px;
          border-left: 1px solid var(--border);
          display: flex;
          flex-direction: column;
          gap: 2px;
        }
        .turn-activity-item {
          border-radius: 6px;
        }
        .turn-activity-row {
          display: flex;
          align-items: baseline;
          gap: 8px;
          width: 100%;
          border: 0;
          background: transparent;
          text-align: left;
          color: var(--subtle-text);
          font: inherit;
          padding: 3px 4px;
          border-radius: 6px;
          cursor: default;
        }
        .turn-activity-item.has-diff .turn-activity-row { cursor: pointer; }
        .turn-activity-item.has-diff .turn-activity-row:hover {
          background: rgba(127,127,127,0.08);
          color: var(--assistant-text);
        }
        .turn-activity-verb {
          flex-shrink: 0;
          color: var(--assistant-text);
          opacity: 0.72;
          min-width: 58px;
        }
        .turn-activity-detail {
          flex: 1;
          min-width: 0;
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }
        .turn-activity-status {
          margin-left: auto;
          flex-shrink: 0;
        }
        .turn-activity-item-counts {
          display: inline-flex;
          gap: 6px;
          flex-shrink: 0;
          font-weight: 600;
          font-variant-numeric: tabular-nums;
        }
        .turn-activity-diff {
          display: none;
          margin: 2px 0 6px 58px;
          max-height: 280px;
          overflow: auto;
          border: 1px solid var(--border);
          border-radius: 8px;
          background: var(--chat-bg);
        }
        .turn-activity-item.open .turn-activity-diff { display: block; }
        .turn-activity-thought {
          display: none;
          margin: 2px 0 6px 58px;
          max-height: 320px;
          overflow: auto;
          padding: 8px 10px;
          border: 1px solid var(--reasoning-border);
          border-radius: 8px;
          background: var(--reasoning-bg);
          color: var(--reasoning-text);
          font-size: 12px;
          line-height: 1.55;
          white-space: pre-wrap;
          word-break: break-word;
        }
        .turn-activity-item.open .turn-activity-thought { display: block; }
        .turn-activity-item.has-thought .turn-activity-row { cursor: pointer; }
        .turn-activity-item.has-thought .turn-activity-row:hover {
          color: var(--assistant-text);
        }
        .diff-line {
          display: flex;
          font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
          font-size: 11px;
          line-height: 1.55;
          white-space: pre-wrap;
          word-break: break-word;
        }
        .diff-line-prefix {
          width: 16px;
          flex-shrink: 0;
          text-align: center;
          opacity: 0.7;
          user-select: none;
        }
        .diff-line-text { flex: 1; min-width: 0; padding-right: 8px; }
        .diff-line.add { background: var(--diff-add-bg); }
        .diff-line.del { background: var(--diff-del-bg); }
        .diff-line.header, .diff-line.hunk {
          color: var(--subtle-text);
          background: transparent;
        }
        .diff-line.collapsed {
          color: var(--subtle-text);
          justify-content: center;
          padding: 4px 8px;
          font-size: 11px;
          border-top: 1px dashed var(--border);
          border-bottom: 1px dashed var(--border);
        }
        .diff-empty {
          padding: 10px 14px;
          font-size: 12px;
          color: var(--subtle-text);
        }
        .tool-body {
          padding: 10px 14px 14px;
          border-top: 1px solid var(--border);
        }
        .tool-header, .tool-summary-text {
          font-size: 12px;
          color: var(--subtle-text);
          margin-bottom: 8px;
          white-space: pre-wrap;
        }
        .tool-section-label {
          font-size: 11px;
          font-weight: 600;
          color: var(--subtle-text);
          margin: 8px 0 4px;
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }
        .tool-result { margin-top: 8px; }
        .tool-approval {
          margin-top: 12px;
          padding: 12px;
          border: 1px solid var(--border);
          border-radius: 12px;
          background: var(--panel);
        }
        .tool-approval-title {
          font-size: 13px;
          font-weight: 600;
          color: var(--assistant-text);
        }
        .tool-approval-description {
          margin-top: 4px;
          font-size: 12px;
          color: var(--subtle-text);
        }
        .tool-approval-arguments {
          max-height: 180px;
          margin-top: 10px;
          overflow: auto;
          white-space: pre-wrap;
          overflow-wrap: anywhere;
        }
        .tool-approval-actions {
          display: flex;
          justify-content: flex-end;
          gap: 8px;
          margin-top: 12px;
        }
        .tool-approval-button {
          min-width: 72px;
          border: 1px solid var(--border);
          border-radius: 8px;
          padding: 6px 14px;
          cursor: pointer;
          font: inherit;
          font-size: 12px;
          font-weight: 600;
        }
        .tool-approval-button.deny {
          color: var(--assistant-text);
          background: var(--panel);
        }
        .tool-approval-button.approve {
          border-color: var(--tool-success-text);
          color: #FFFFFF;
          background: var(--tool-success-text);
        }
        .tool-approval-button:disabled {
          cursor: default;
          opacity: 0.55;
        }
        .tool-approval-result {
          margin-top: 10px;
          font-size: 12px;
          font-weight: 600;
        }
        .tool-approval-result.approved { color: var(--tool-success-text); }
        .tool-approval-result.denied { color: var(--tool-failure-text); }
        """;

    private static string GetThemeStyles() =>
        GetThemeTokenStyles() + GetCodeSyntaxOverrideStyles() + GetStaticShellStyles();

    private static string GetTimelineScript() =>
        """
        const state = {
          currentAssistantEl: null,
          currentReasoningEl: null,
          assistantStarted: {},
          reasoningStarted: {},
          toolCalls: new Map(),
          trackReasoningDuration: true,
          reasoningStartAt: {},
          reasoningFinalizedMs: {},
          batching: false,
          pendingEnhancementRoots: [],
          scrollFrame: 0,
          scrollForcePending: false,
          autoScrollEnabled: true,
          batchTarget: null
        };

        function t(key) {
          return (window.__chatI18n && window.__chatI18n[key]) || key;
        }

        function applyChatI18n() {
          const loadOlder = document.getElementById('load-older');
          if (loadOlder) loadOlder.textContent = t('loadOlder');
          document.querySelectorAll('.code-btn').forEach(function (btn) {
            if (btn.classList.contains('copied')) return;
            if (btn.dataset.i18n === 'preview') {
              btn.textContent = t('preview');
              return;
            }
            btn.textContent = t('copy');
          });
          document.querySelectorAll('[data-i18n]').forEach(function (element) {
            element.textContent = t(element.dataset.i18n);
          });
          document.querySelectorAll('.reasoning-label').forEach(function (label) {
            const row = label.closest('.reasoning-row');
            const messageId = row && row.dataset.messageId;
            if (messageId && state.reasoningFinalizedMs[messageId] !== undefined) {
              finalizeReasoningLabel(messageId);
            } else if (messageId && state.reasoningStartAt[messageId]) {
              updateReasoningThinkingLabel(messageId);
            } else if (!label.textContent || label.textContent.indexOf('思考') >= 0 || label.textContent.indexOf('Think') >= 0) {
              label.textContent = t('thinking');
            }
          });
        }

        function cssEscape(value) {
          if (window.CSS && typeof CSS.escape === 'function') return CSS.escape(String(value));
          return String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
        }

        function decodeBase64Utf8(b64) {
          const binary = atob(b64);
          const bytes = new Uint8Array(binary.length);
          for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
          return new TextDecoder('utf-8').decode(bytes);
        }

        function resolveEventMarkdown(event) {
          if (event && event.markdownB64) return decodeBase64Utf8(event.markdownB64);
          if (event && event.markdown) return event.markdown;
          if (event && event.content) return event.content;
          return '';
        }

        function resolveEventHtml(event) {
          if (event && event.htmlB64) return decodeBase64Utf8(event.htmlB64);
          return (event && event.html) || '';
        }

        function resolveRenderedHtml(event, fallbackText) {
          const html = resolveEventHtml(event);
          if (html) return html;
          return '<pre>' + escapeHtml(resolveEventMarkdown(event) || fallbackText || '') + '</pre>';
        }

        function escapeHtml(text) {
          const div = document.createElement('div');
          div.textContent = text == null ? '' : String(text);
          return div.innerHTML;
        }

        function post(payload) {
          if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(payload);
          }
        }

        function getChatScroller() {
          return document.getElementById('chat-scroll');
        }

        function getMessageRoot() {
          return state.batchTarget || document.getElementById('messages');
        }

        function isNearBottom() {
          const scroller = getChatScroller();
          return !scroller || scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight <= 80;
        }

        function hasActiveSelection() {
          const selection = window.getSelection && window.getSelection();
          return !!selection && !selection.isCollapsed && String(selection).length > 0;
        }

        function scrollToBottom(force) {
          if (state.batching || (!force && (!state.autoScrollEnabled || hasActiveSelection()))) return;
          if (force) state.scrollForcePending = true;
          if (state.scrollFrame) return;
          state.scrollFrame = requestAnimationFrame(function () {
            state.scrollFrame = 0;
            const shouldForce = state.scrollForcePending;
            state.scrollForcePending = false;
            const scroller = getChatScroller();
            if (state.batching || !scroller
                || (!shouldForce && (!state.autoScrollEnabled || hasActiveSelection()))) return;
            scroller.scrollTop = scroller.scrollHeight;
          });
        }

        function updateEmptyStateVisibility() {
          if (state.batching) return;
          const emptyState = document.getElementById('empty-state');
          const root = document.getElementById('messages');
          if (!emptyState || !root) return;
          emptyState.style.display = root.children.length === 0 ? 'flex' : 'none';
        }

        function findAssistantContentNode(messageId) {
          if (!messageId) return null;
          const row = document.querySelector('.message-row.assistant-row[data-message-id="' + cssEscape(messageId) + '"]');
          return row ? row.querySelector('.bubble > .message-content') : null;
        }

        function findAssistantBubbleRow(messageId) {
          if (!messageId) return null;
          return document.querySelector('.message-row.assistant-row[data-message-id="' + cssEscape(messageId) + '"]');
        }

        function applyMarkdownHtml(node, html, enhance) {
          if (!node) return;
          node.classList.add('md-root');
          node.innerHTML = html || '';
          if (enhance === false) return;
          if (state.batching) {
            state.pendingEnhancementRoots.push(node);
          } else {
            enhanceCodeBlocks(node);
          }
        }

        function applyAssistantHtml(messageId, html, createIfMissing, streaming) {
          let row = findAssistantBubbleRow(messageId);
          if (!row && createIfMissing) {
            row = createAssistantRow(messageId);
            getMessageRoot().appendChild(row);
            state.assistantStarted[messageId] = true;
            state.currentAssistantEl = row;
          }
          applyMarkdownHtml(findAssistantContentNode(messageId), html, streaming !== true);
          updateEmptyStateVisibility();
          scrollToBottom();
        }

        const codeObserver = typeof IntersectionObserver === 'function'
          ? new IntersectionObserver(function (entries, observer) {
              entries.forEach(function (entry) {
                if (!entry.isIntersecting) return;
                const code = entry.target;
                observer.unobserve(code);
                if (typeof hljs !== 'undefined' && !code.dataset.hljsDone) {
                  try {
                    hljs.highlightElement(code);
                    code.dataset.hljsDone = '1';
                  } catch (e) {}
                }
              });
            }, { root: document.getElementById('chat-scroll'), rootMargin: '200px 0px' })
          : null;

        function enhanceCodeBlocks(root) {
          const scope = root || document;
          scope.querySelectorAll('.md-root pre').forEach(function (pre, index) {
            if (pre.closest('.code-block')) return;
            const code = pre.querySelector('code');
            if (!code) return;

            const raw = code.textContent || '';
            const className = code.className || '';
            const match = className.match(/language-([\w#+-]+)/i);
            const language = match ? match[1] : t('code');

            const wrapper = document.createElement('div');
            wrapper.className = 'code-block';

            const header = document.createElement('div');
            header.className = 'code-block-header';

            const label = document.createElement('span');
            label.textContent = language;

            const actions = document.createElement('div');
            actions.className = 'code-block-actions';

            const langKey = (match ? match[1] : '').toLowerCase();
            if (langKey === 'html' || langKey === 'htm') {
              const previewBtn = document.createElement('button');
              previewBtn.type = 'button';
              previewBtn.className = 'code-btn';
              previewBtn.dataset.i18n = 'preview';
              previewBtn.textContent = t('preview');
              previewBtn.addEventListener('click', function () {
                post({ type: 'preview', html: raw });
              });
              actions.appendChild(previewBtn);
            }

            const copyBtn = document.createElement('button');
            copyBtn.type = 'button';
            copyBtn.className = 'code-btn';
            copyBtn.textContent = t('copy');
            copyBtn.addEventListener('click', function () {
              post({ type: 'copy', text: raw, blockId: String(index) });
              copyBtn.textContent = t('copied');
              copyBtn.classList.add('copied');
              setTimeout(function () {
                copyBtn.textContent = t('copy');
                copyBtn.classList.remove('copied');
              }, 1600);
            });
            actions.appendChild(copyBtn);

            header.appendChild(label);
            header.appendChild(actions);

            pre.parentNode.insertBefore(wrapper, pre);
            wrapper.appendChild(header);
            wrapper.appendChild(pre);
            if (codeObserver) {
              codeObserver.observe(code);
            } else if (typeof hljs !== 'undefined' && !code.dataset.hljsDone) {
              try {
                hljs.highlightElement(code);
                code.dataset.hljsDone = '1';
              } catch (e) {}
            }
          });
        }

        function resetTimeline() {
          const root = document.getElementById('messages');
          if (codeObserver) codeObserver.disconnect();
          root.innerHTML = '';
          state.currentAssistantEl = null;
          state.currentReasoningEl = null;
          state.assistantStarted = {};
          state.reasoningStarted = {};
          state.reasoningStartAt = {};
          state.reasoningFinalizedMs = {};
          state.toolCalls.clear();
        }

        function beginBatch() {
          state.batching = true;
          if (state.scrollFrame) cancelAnimationFrame(state.scrollFrame);
          state.scrollFrame = 0;
          state.scrollForcePending = false;
          state.pendingEnhancementRoots = [];
          document.documentElement.classList.add('replaying');
        }

        function endBatch(forceScroll) {
          state.batching = false;
          document.documentElement.classList.remove('replaying');
          const roots = state.pendingEnhancementRoots;
          state.pendingEnhancementRoots = [];
          roots.forEach(function (root) { enhanceCodeBlocks(root); });
          updateEmptyStateVisibility();
          scrollToBottom(!!forceScroll);
        }

        function formatReasoningSeconds(ms) {
          return t('seconds').replace('{0}', String(Math.max(1, Math.round(ms / 1000))));
        }

        function findReasoningRow(messageId) {
          if (state.currentReasoningEl
              && String(state.currentReasoningEl.dataset.messageId || '') === String(messageId || '')) {
            return state.currentReasoningEl;
          }
          if (!messageId) return null;
          return document.querySelector('.reasoning-row[data-message-id="' + cssEscape(messageId) + '"]');
        }

        function setReasoningLabelOnRow(row, text) {
          if (!row) return;
          const label = row.querySelector('.reasoning-label');
          if (label) label.textContent = text;
        }

        function setReasoningLabel(messageId, text) {
          setReasoningLabelOnRow(findReasoningRow(messageId), text);
        }

        function getReasoningElapsedMs(messageId) {
          const start = state.reasoningStartAt[messageId];
          return start ? performance.now() - start : 0;
        }

        function updateReasoningThinkingLabel(messageId) {
          if (!state.trackReasoningDuration) {
            setReasoningLabel(messageId, t('thinking'));
            return;
          }
          setReasoningLabel(
            messageId,
            t('thinking') + ' (' + formatReasoningSeconds(getReasoningElapsedMs(messageId)) + ')');
        }

        function finalizeReasoningLabel(messageId) {
          if (!messageId) return;
          const row = findReasoningRow(messageId);
          if (!row) return;
          if (!state.trackReasoningDuration) {
            setReasoningLabelOnRow(row, t('thought'));
            delete state.reasoningStartAt[messageId];
            delete state.reasoningFinalizedMs[messageId];
            return;
          }
          if (state.reasoningFinalizedMs[messageId] !== undefined) {
            return;
          }
          const ms = getReasoningElapsedMs(messageId);
          state.reasoningFinalizedMs[messageId] = ms;
          setReasoningLabelOnRow(row, t('thought') + ' (' + formatReasoningSeconds(ms) + ')');
          delete state.reasoningStartAt[messageId];
        }

        function openImagePreview(url, fileName) {
          var lightbox = document.getElementById('image-lightbox');
          if (!lightbox || !url) return;
          var img = lightbox.querySelector('.image-lightbox-img');
          if (img) {
            img.src = url;
            img.alt = fileName || '';
          }
          lightbox.hidden = false;
          document.body.style.overflow = 'hidden';
        }

        function closeImagePreview() {
          var lightbox = document.getElementById('image-lightbox');
          if (!lightbox) return;
          lightbox.hidden = true;
          var img = lightbox.querySelector('.image-lightbox-img');
          if (img) {
            img.removeAttribute('src');
            img.alt = '';
          }
          document.body.style.overflow = '';
        }

        function createUserRow(content, images) {
          const row = document.createElement('div');
          row.className = 'message-row user';
          const bubble = document.createElement('div');
          bubble.className = 'bubble';

          if (images && images.length) {
            const gallery = document.createElement('div');
            gallery.className = 'user-images';
            images.forEach(function (image) {
              if (!image || !image.url) return;
              const thumb = document.createElement('img');
              thumb.className = 'user-image-thumb';
              thumb.src = image.url;
              thumb.alt = image.fileName || '';
              thumb.title = image.fileName || '';
              thumb.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                openImagePreview(image.url, image.fileName);
              });
              gallery.appendChild(thumb);
            });
            if (gallery.childNodes.length) bubble.appendChild(gallery);
          }

          if (content) {
            const text = document.createElement('div');
            text.className = 'message-content user-text';
            text.textContent = content;
            bubble.appendChild(text);
          }

          row.appendChild(bubble);
          return row;
        }

        function createAssistantRow(messageId) {
          const row = document.createElement('div');
          row.className = 'message-row assistant assistant-row';
          row.dataset.messageId = messageId || '';
          row.innerHTML =
            '<div class="bubble">' +
              '<div class="message-content md-root"></div>' +
            '</div>';
          return row;
        }

        function createReasoningRow(messageId) {
          const row = document.createElement('div');
          row.className = 'message-row assistant reasoning-row';
          row.dataset.messageId = messageId || '';
          row.innerHTML =
            '<details class="reasoning-block" open>' +
              '<summary><span class="reasoning-chevron">›</span><span class="reasoning-label">' + t('thinking') + '</span></summary>' +
              '<div class="reasoning-content message-content"></div>' +
            '</details>';
          return row;
        }

        function appendMessage(role, content, append, images) {
          if (append && role === 'assistant' && state.currentAssistantEl) {
            const el = state.currentAssistantEl.querySelector('.message-content');
            el.textContent += content;
            scrollToBottom();
            return;
          }
          if (append && role === 'reasoning' && state.currentReasoningEl) {
            const el = state.currentReasoningEl.querySelector('.reasoning-content');
            el.textContent += content;
            scrollToBottom();
            return;
          }

          if (role === 'user') {
            getMessageRoot().appendChild(createUserRow(content, images));
          } else if (role === 'assistant') {
            const row = createAssistantRow('');
            row.querySelector('.message-content').textContent = content;
            getMessageRoot().appendChild(row);
            state.currentAssistantEl = row;
          } else if (role === 'reasoning') {
            const row = createReasoningRow('');
            row.querySelector('.reasoning-content').textContent = content;
            getMessageRoot().appendChild(row);
            state.currentReasoningEl = row;
          }
          updateEmptyStateVisibility();
          scrollToBottom();
        }

        function ensureAssistantBubble(messageId) {
          if (state.currentAssistantEl && state.assistantStarted[messageId]) return;
          const row = createAssistantRow(messageId);
          getMessageRoot().appendChild(row);
          state.currentAssistantEl = row;
          state.assistantStarted[messageId] = true;
          updateEmptyStateVisibility();
        }

        function ensureReasoningBubble(messageId) {
          if (state.currentReasoningEl && state.reasoningStarted[messageId]) return;
          const row = createReasoningRow(messageId);
          getMessageRoot().appendChild(row);
          state.currentReasoningEl = row;
          state.reasoningStarted[messageId] = true;
          updateEmptyStateVisibility();
        }

        function createToolCard(toolCallId, toolName) {
          state.currentAssistantEl = null;
          state.currentReasoningEl = null;
          const row = document.createElement('div');
          row.className = 'message-row assistant tool-row';
          const details = document.createElement('details');
          details.className = 'message tool';
          details.dataset.toolCallId = toolCallId;
          details.innerHTML =
            '<summary><span>' + escapeHtml(toolName || 'unknown') + '</span>' +
            '<span class="tool-status running">running</span></summary>' +
            '<div class="tool-body">' +
            '<div class="tool-section-label">arguments</div>' +
            '<pre class="tool-pre tool-args"></pre>' +
            '<div class="tool-result" style="display:none">' +
            '<div class="tool-section-label">result</div>' +
            '<div class="tool-result-html md-root"></div>' +
            '</div></div>';
          row.appendChild(details);
          getMessageRoot().appendChild(row);
          state.toolCalls.set(toolCallId, details);
          updateEmptyStateVisibility();
          scrollToBottom();
        }

        function getToolCard(toolCallId) {
          return state.toolCalls.get(toolCallId) || document.querySelector('[data-tool-call-id="' + toolCallId + '"]');
        }

        function applyToolStatusBadge(badge, status) {
          if (!badge) return;
          const normalized = (status || 'succeeded').toLowerCase();
          if (normalized === 'awaiting_approval') {
            badge.textContent = t('approvalPending');
            badge.className = 'tool-status running';
            return;
          }
          if (normalized === 'approval_denied') {
            badge.textContent = t('deniedStatus');
            badge.className = 'tool-status failed';
            return;
          }
          const cssClass = normalized === 'succeeded' || normalized === 'success'
            ? 'success'
            : normalized === 'failed' || normalized === 'failure'
              ? 'failed'
              : normalized === 'cancelled' || normalized === 'canceled'
                ? 'cancelled'
                : normalized === 'running'
                  ? 'running'
                  : normalized === 'preparing'
                    ? 'running'
                    : 'success';
          const label = cssClass === 'success'
            ? 'success'
            : cssClass === 'failed'
              ? 'failed'
              : cssClass === 'cancelled'
                ? 'cancelled'
                : cssClass === 'running'
                  ? 'running'
                  : normalized;
          badge.textContent = label;
          badge.className = 'tool-status ' + cssClass;
        }

        function ensureToolApprovalPanel(card, event) {
          const body = card.querySelector('.tool-body');
          if (!body) return null;

          let panel = body.querySelector('.tool-approval');
          if (panel) return panel;

          panel = document.createElement('div');
          panel.className = 'tool-approval';
          body.prepend(panel);

          const title = document.createElement('div');
          title.className = 'tool-approval-title';
          title.dataset.i18n = 'approvalTitle';
          title.textContent = t('approvalTitle');
          panel.appendChild(title);

          const description = document.createElement('div');
          description.className = 'tool-approval-description';
          description.dataset.i18n = 'approvalDescription';
          description.textContent = t('approvalDescription');
          panel.appendChild(description);

          const argumentsPre = document.createElement('pre');
          argumentsPre.className = 'tool-pre tool-approval-arguments';
          panel.appendChild(argumentsPre);

          const actions = document.createElement('div');
          actions.className = 'tool-approval-actions';
          const deny = document.createElement('button');
          deny.type = 'button';
          deny.className = 'tool-approval-button deny';
          deny.dataset.i18n = 'deny';
          deny.textContent = t('deny');
          const approve = document.createElement('button');
          approve.type = 'button';
          approve.className = 'tool-approval-button approve';
          approve.dataset.i18n = 'approve';
          approve.textContent = t('approve');

          function submit(approved) {
            deny.disabled = true;
            approve.disabled = true;
            post({ type: 'toolApproval', toolCallId: event.toolCallId, approved: approved });
          }
          deny.addEventListener('click', function () { submit(false); });
          approve.addEventListener('click', function () { submit(true); });
          actions.appendChild(deny);
          actions.appendChild(approve);
          panel.appendChild(actions);
          return panel;
        }

        function showToolApproval(event) {
          let card = getToolCard(event.toolCallId);
          if (!card) {
            createToolCard(event.toolCallId, event.toolName);
            card = getToolCard(event.toolCallId);
          }
          if (!card) return;

          card.open = true;
          card.dataset.awaitingApproval = 'true';
          const badge = card.querySelector('.tool-status');
          if (badge) {
            badge.textContent = t('approvalPending');
            badge.className = 'tool-status running';
          }

          const panel = ensureToolApprovalPanel(card, event);
          const argumentsPre = panel && panel.querySelector('.tool-approval-arguments');
          if (argumentsPre) argumentsPre.textContent = event.arguments || '';
          const argsPre = card.querySelector('.tool-args');
          if (argsPre && event.arguments) argsPre.textContent = event.arguments;
          scrollToBottom(true);
        }

        function resolveToolApproval(event) {
          const card = getToolCard(event.toolCallId);
          const panel = card && card.querySelector('.tool-approval');
          if (!card || !panel) return;

          delete card.dataset.awaitingApproval;
          const badge = card.querySelector('.tool-status');
          if (badge) {
            badge.textContent = t(event.approved ? 'allowedStatus' : 'deniedStatus');
            badge.className = 'tool-status ' + (event.approved ? 'success' : 'failed');
          }
          const actions = panel.querySelector('.tool-approval-actions');
          if (actions) actions.remove();
          let result = panel.querySelector('.tool-approval-result');
          if (!result) {
            result = document.createElement('div');
            result.className = 'tool-approval-result';
            panel.appendChild(result);
          }
          const decisionKey = event.approved ? 'approved' : 'denied';
          result.dataset.i18n = decisionKey;
          result.textContent = t(decisionKey);
          result.className = 'tool-approval-result ' + decisionKey;
        }

        function renderDiffLines(lines) {
          if (!lines || !lines.length) {
            return '<div class="diff-empty">' + escapeHtml(t('noDiffAvailable')) + '</div>';
          }
          return lines.map(function (line) {
            var kind = (line.kind || '').toLowerCase();
            if (kind === 'collapsed') {
              var label = (t('unmodifiedLines') || '{0} unmodified lines').replace('{0}', String(line.count || 0));
              return '<div class="diff-line collapsed">' + escapeHtml(label) + '</div>';
            }
            var css = kind === 'added' ? 'add'
              : kind === 'removed' ? 'del'
              : kind === 'hunkheader' ? 'hunk'
              : kind === 'header' ? 'header'
              : 'ctx';
            var prefix = kind === 'added' ? '+'
              : kind === 'removed' ? '-'
              : kind === 'hunkheader' || kind === 'header' ? ''
              : ' ';
            return '<div class="diff-line ' + css + '">' +
              '<span class="diff-line-prefix">' + escapeHtml(prefix) + '</span>' +
              '<span class="diff-line-text">' + escapeHtml(line.text || '') + '</span></div>';
          }).join('');
        }

        function filesChangedTitle(count) {
          if (count === 1) return t('filesChangedOne') || '1 File Changed';
          return (t('filesChangedMany') || '{0} Files Changed').replace('{0}', String(count));
        }

        function joinSummaryParts(parts) {
          if (!parts.length) return '';
          if (parts.length === 1) return parts[0];
          if (parts.length === 2) return parts[0] + ', ' + parts[1];
          return parts.slice(0, -1).join(', ') + ', ' + parts[parts.length - 1];
        }

        function turnActivitySummaryText(event) {
          var parts = [];
          var explored = event.exploredFileCount || 0;
          var searches = event.searchCount || 0;
          var commands = event.commandCount || 0;
          var thoughts = event.thoughtCount || 0;
          if (explored === 1) parts.push(t('exploredFilesOne') || 'explored 1 file');
          else if (explored > 1) parts.push((t('exploredFilesMany') || 'explored {0} files').replace('{0}', String(explored)));
          if (searches === 1) parts.push(t('searchesOne') || '1 search');
          else if (searches > 1) parts.push((t('searchesMany') || '{0} searches').replace('{0}', String(searches)));
          if (commands === 1) parts.push(t('commandsOne') || 'ran 1 command');
          else if (commands > 1) parts.push((t('commandsMany') || 'ran {0} commands').replace('{0}', String(commands)));
          if (parts.length === 0 && thoughts > 0) {
            if (thoughts === 1) return t('thoughtsOne') || t('thought') || 'Thought';
            return (t('thoughtsMany') || '{0} thoughts').replace('{0}', String(thoughts));
          }
          return joinSummaryParts(parts) || (t('thought') || 'Activity');
        }

        function appendFilesChangedCard(event) {
          state.currentAssistantEl = null;
          state.currentReasoningEl = null;
          var files = event.files || [];
          if (!files.length) return;

          var existing = document.querySelector('.files-changed-card[data-live="1"]');
          var card = existing;
          var openPaths = {};
          if (existing) {
            existing.querySelectorAll('.files-changed-item.open').forEach(function (item) {
              var path = item.getAttribute('data-path') || '';
              if (path) openPaths[path] = true;
            });
            card.innerHTML = '';
          } else {
            var row = document.createElement('div');
            row.className = 'message-row assistant';
            card = document.createElement('div');
            card.className = 'files-changed-card';
            if (event.upsert) card.setAttribute('data-live', '1');
            row.appendChild(card);
            getMessageRoot().appendChild(row);
          }

          var title = document.createElement('div');
          title.className = 'files-changed-title';
          title.textContent = filesChangedTitle(files.length);
          card.appendChild(title);

          var list = document.createElement('div');
          list.className = 'files-changed-list';

          files.forEach(function (file) {
            var item = document.createElement('div');
            item.className = 'files-changed-item';
            item.setAttribute('data-path', file.path || '');
            if (openPaths[file.path || '']) item.classList.add('open');

            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'files-changed-row';
            button.title = file.path || file.displayName || '';

            var name = document.createElement('span');
            name.className = 'files-changed-name';
            name.textContent = file.displayName || file.path || '';
            button.appendChild(name);

            var counts = document.createElement('span');
            counts.className = 'files-changed-counts';
            if ((file.added || 0) > 0) {
              var a = document.createElement('span');
              a.className = 'turn-activity-add';
              a.textContent = '+' + file.added;
              counts.appendChild(a);
            }
            if ((file.removed || 0) > 0) {
              var d = document.createElement('span');
              d.className = 'turn-activity-del';
              d.textContent = '-' + file.removed;
              counts.appendChild(d);
            }
            button.appendChild(counts);
            item.appendChild(button);

            var diff = document.createElement('div');
            diff.className = 'files-changed-diff';
            diff.innerHTML = renderDiffLines(file.lines || []);
            button.addEventListener('click', function (e) {
              e.preventDefault();
              e.stopPropagation();
              item.classList.toggle('open');
              scrollToBottom();
            });
            item.appendChild(diff);
            list.appendChild(item);
          });

          card.appendChild(list);
          if (event.upsert) {
            card.setAttribute('data-live', '1');
          } else {
            card.removeAttribute('data-live');
          }
          updateEmptyStateVisibility();
          scrollToBottom();
        }

        function appendTurnActivityCard(event) {
          state.currentAssistantEl = null;
          state.currentReasoningEl = null;
          var items = event.items || [];
          if (!items.length && !(event.exploredFileCount || event.searchCount || event.commandCount || event.thoughtCount)) return;

          // Live cards always upsert the current open segment; sealing (upsert=false) finalizes that card.
          var existing = document.querySelector('.turn-activity[data-live="1"]');
          var details = existing;
          var wasOpen = existing ? existing.open : false;
          if (!details) {
            var row = document.createElement('div');
            row.className = 'message-row assistant';
            details = document.createElement('details');
            details.className = 'turn-activity';
            if (event.upsert) details.setAttribute('data-live', '1');
            row.appendChild(details);
            getMessageRoot().appendChild(row);
          } else {
            details.innerHTML = '';
          }

          var summary = document.createElement('summary');
          var summaryText = document.createElement('span');
          summaryText.className = 'turn-activity-summary-text';
          summaryText.textContent = turnActivitySummaryText(event);
          summary.appendChild(summaryText);

          var chevron = document.createElement('span');
          chevron.className = 'turn-activity-chevron';
          chevron.textContent = '›';
          summary.appendChild(chevron);
          details.appendChild(summary);

          var body = document.createElement('div');
          body.className = 'turn-activity-body';

          items.forEach(function (item) {
            var hasDiff = item.lines && item.lines.length;
            var hasThought = item.kind === 'thought' && item.body;
            var entry = document.createElement('div');
            entry.className = 'turn-activity-item'
              + (hasDiff ? ' has-diff' : '')
              + (hasThought ? ' has-thought' : '');

            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'turn-activity-row';
            button.title = item.path || item.detail || '';

            var verb = document.createElement('span');
            verb.className = 'turn-activity-verb';
            verb.textContent = item.verb || '';

            var detail = document.createElement('span');
            detail.className = 'turn-activity-detail';
            detail.textContent = item.detail || item.path || '';

            button.appendChild(verb);
            button.appendChild(detail);

            if (item.status) {
              var status = document.createElement('span');
              status.className = 'turn-activity-status tool-status';
              applyToolStatusBadge(status, item.status);
              if (item.statusLabel) status.textContent = item.statusLabel;
              button.appendChild(status);
            }

            entry.appendChild(button);

            if (hasDiff) {
              var diff = document.createElement('div');
              diff.className = 'turn-activity-diff';
              diff.innerHTML = renderDiffLines(item.lines);
              button.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                entry.classList.toggle('open');
                scrollToBottom();
              });
              entry.appendChild(diff);
            } else if (hasThought) {
              var thought = document.createElement('div');
              thought.className = 'turn-activity-thought';
              thought.textContent = item.body || '';
              button.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                entry.classList.toggle('open');
                scrollToBottom();
              });
              entry.appendChild(thought);
            }

            body.appendChild(entry);
          });

          details.appendChild(body);
          if (wasOpen) details.open = true;
          if (event.upsert) {
            details.setAttribute('data-live', '1');
          } else {
            details.removeAttribute('data-live');
          }
          updateEmptyStateVisibility();
          scrollToBottom();
        }

        function handleEvent(event) {
          if (!event || !event.type) return;
          switch (event.type) {
            case 'RESET_TIMELINE':
              resetTimeline();
              updateEmptyStateVisibility();
              break;
            case 'USER_MESSAGE':
              (function () {
                var liveActivity = document.querySelector('.turn-activity[data-live="1"]');
                if (liveActivity) liveActivity.removeAttribute('data-live');
                var liveFiles = document.querySelector('.files-changed-card[data-live="1"]');
                if (liveFiles) liveFiles.removeAttribute('data-live');
              })();
              appendMessage('user', event.content || '', false, event.images || []);
              break;
            case 'FILES_CHANGED':
              appendFilesChangedCard(event);
              break;
            case 'TURN_ACTIVITY':
              appendTurnActivityCard(event);
              break;
            case 'RUN_STARTED':
              state.currentAssistantEl = null;
              state.currentReasoningEl = null;
              break;
            case 'REASONING_MESSAGE_START':
            case 'REASONING_MESSAGE_CONTENT':
            case 'REASONING_MESSAGE_END':
              // Reasoning is folded into TURN_ACTIVITY; ignore standalone thought bubbles.
              break;
            case 'TEXT_MESSAGE_START':
              state.currentAssistantEl = null;
              state.assistantStarted[event.messageId] = false;
              break;
            case 'TEXT_MESSAGE_CONTENT':
              // Plain-text deltas are unused for display; live Markdown arrives via STATIC_ASSISTANT_HTML.
              finalizeReasoningLabel(event.messageId);
              if (!state.assistantStarted[event.messageId]) ensureAssistantBubble(event.messageId);
              break;
            case 'TEXT_MESSAGE_END':
              state.currentAssistantEl = null;
              break;
            case 'STATIC_ASSISTANT_HTML':
              applyAssistantHtml(
                event.messageId,
                resolveRenderedHtml(event),
                event.createIfMissing !== false,
                event.streaming === true);
              if (!event.streaming) state.currentAssistantEl = null;
              break;
            case 'TOOL_CALL_START':
              createToolCard(event.toolCallId, event.toolCallName);
              break;
            case 'TOOL_CALL_ARGS': {
              const card = getToolCard(event.toolCallId);
              const pre = card && card.querySelector('.tool-args');
              // delta is the full JSON snapshot built so far, not an incremental chunk
              if (pre) pre.textContent = event.delta || '';
              scrollToBottom();
              break;
            }
            case 'TOOL_CALL_END': {
              const card = getToolCard(event.toolCallId);
              if (!card) break;
              const normalized = (event.status || 'running').toLowerCase();
              if (normalized === 'awaiting_approval' || normalized === 'approval_denied') {
                applyToolStatusBadge(card.querySelector('.tool-status'), normalized);
                if (normalized === 'awaiting_approval') {
                  card.dataset.awaitingApproval = 'true';
                }
                break;
              }
              const panel = card.querySelector('.tool-approval');
              const hasPendingApproval = panel && panel.querySelector('.tool-approval-actions');
              if (card.dataset.awaitingApproval !== 'true' && !hasPendingApproval) {
                applyToolStatusBadge(card.querySelector('.tool-status'), event.status || 'running');
              }
              break;
            }
            case 'TOOL_APPROVAL_REQUEST':
              showToolApproval(event);
              break;
            case 'TOOL_APPROVAL_RESOLVED':
              resolveToolApproval(event);
              break;
            case 'TOOL_CALL_OUTPUT': {
              const card = getToolCard(event.toolCallId);
              const result = card && card.querySelector('.tool-result');
              const html = card && card.querySelector('.tool-result-html');
              if (result && html) {
                result.style.display = 'block';
                html.textContent += event.delta || '';
              }
              scrollToBottom();
              break;
            }
            case 'TOOL_CALL_RESULT': {
              const card = getToolCard(event.toolCallId);
              if (!card) break;
              applyToolStatusBadge(card.querySelector('.tool-status'), event.status || 'succeeded');
              if (event.header) {
                let header = card.querySelector('.tool-header');
                if (!header) {
                  header = document.createElement('div');
                  header.className = 'tool-header';
                  card.querySelector('.tool-body').prepend(header);
                }
                header.textContent = event.header;
              }
              if (event.summary) {
                let summary = card.querySelector('.tool-summary-text');
                if (!summary) {
                  summary = document.createElement('div');
                  summary.className = 'tool-summary-text';
                  card.querySelector('.tool-body').insertBefore(summary, card.querySelector('.tool-result'));
                }
                summary.textContent = event.summary;
              }
              const result = card.querySelector('.tool-result');
              const html = card.querySelector('.tool-result-html');
              if (result && html) {
                result.style.display = 'block';
                applyMarkdownHtml(html, resolveRenderedHtml(event, event.content || ''));
              }
              scrollToBottom();
              break;
            }
          }
        }

        function applyThemeTokensToRoot(tokensCss) {
          var root = document.documentElement;
          root.style.cssText = '';
          tokensCss.replace(/(--[\\w-]+)\\s*:\\s*([^;]+);/g, function(_, name, value) {
            root.style.setProperty(name.trim(), value.trim());
          });
        }

        function syncThemeSurfaces() {
          var rootStyle = getComputedStyle(document.documentElement);
          var chatBg = rootStyle.getPropertyValue('--chat-bg').trim();
          var assistantText = rootStyle.getPropertyValue('--assistant-text').trim();
          if (chatBg) {
            document.documentElement.style.backgroundColor = chatBg;
            document.body.style.backgroundColor = chatBg;
            var scroller = document.getElementById('chat-scroll');
            if (scroller) scroller.style.backgroundColor = chatBg;
          }
          if (assistantText) {
            document.body.style.color = assistantText;
          }
        }

        function applyThemeUpdate(highlightHref, tokensB64, syntaxB64) {
          var link = document.querySelector('head link[rel="stylesheet"]');
          if (link) {
            link.href = highlightHref;
          }
          var tokensCss = decodeBase64Utf8(tokensB64);
          var tokensEl = document.getElementById('chat-theme-tokens');
          if (tokensEl) {
            tokensEl.textContent = tokensCss;
          }
          var syntaxEl = document.getElementById('chat-code-syntax');
          if (syntaxEl) {
            syntaxEl.textContent = decodeBase64Utf8(syntaxB64);
          }
          applyThemeTokensToRoot(tokensCss);
          syncThemeSurfaces();
        }

        function replayEvents(events) {
          beginBatch();
          state.trackReasoningDuration = false;
          resetTimeline();
          for (const raw of events) {
            try {
              const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
              handleEvent(event);
            } catch (e) { console.warn('replayEvents parse failed', e); }
          }
          state.trackReasoningDuration = true;
          endBatch(true);
        }

        function setOlderMessagesAvailable(available) {
          const button = document.getElementById('load-older');
          if (!button) return;
          button.hidden = !available;
          button.disabled = false;
          button.textContent = t('loadOlder');
        }

        function prependEvents(events, hasOlderMessages) {
          const scroller = getChatScroller();
          const root = document.getElementById('messages');
          if (!scroller || !root) return;
          const previousHeight = scroller.scrollHeight;
          const previousTop = scroller.scrollTop;
          const fragment = document.createDocumentFragment();
          beginBatch();
          state.batchTarget = fragment;
          for (const raw of events) {
            try {
              const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
              handleEvent(event);
            } catch (e) { console.warn('prependEvents parse failed', e); }
          }
          state.batchTarget = null;
          root.insertBefore(fragment, root.firstChild);
          endBatch(false);
          setOlderMessagesAvailable(!!hasOlderMessages);
          scroller.scrollTop = previousTop + (scroller.scrollHeight - previousHeight);
          state.currentAssistantEl = null;
          state.currentReasoningEl = null;
        }

        function handleWebMessage(message) {
          const command = typeof message === 'string' ? JSON.parse(message) : message;
          if (!command || !command.command) return;
          if (command.command === 'replay') {
            replayEvents(Array.isArray(command.events) ? command.events : []);
          } else if (command.command === 'prepend') {
            prependEvents(
              Array.isArray(command.events) ? command.events : [],
              !!command.hasOlderMessages);
          } else if (command.command === 'historyAvailability') {
            setOlderMessagesAvailable(!!command.hasOlderMessages);
          } else if (command.command === 'reset') {
            beginBatch();
            resetTimeline();
            endBatch(false);
          }
        }

        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.addEventListener('message', function (event) {
            try { handleWebMessage(event.data); }
            catch (e) { console.warn('chat web message failed', e); }
          });
        }

        const chatScroller = getChatScroller();
        if (chatScroller) {
          chatScroller.addEventListener('scroll', function () {
            state.autoScrollEnabled = isNearBottom();
          }, { passive: true });
        }
        const loadOlderButton = document.getElementById('load-older');
        if (loadOlderButton) {
          loadOlderButton.textContent = t('loadOlder');
          loadOlderButton.addEventListener('click', function () {
            loadOlderButton.disabled = true;
            post({ type: 'loadOlder' });
          });
        }
        document.addEventListener('selectionchange', function () {
          if (hasActiveSelection()) state.autoScrollEnabled = false;
          else if (isNearBottom()) state.autoScrollEnabled = true;
        });

        (function bindImageLightbox() {
          var lightbox = document.getElementById('image-lightbox');
          if (!lightbox) return;
          var backdrop = lightbox.querySelector('.image-lightbox-backdrop');
          var closeBtn = lightbox.querySelector('.image-lightbox-close');
          if (backdrop) backdrop.addEventListener('click', closeImagePreview);
          if (closeBtn) closeBtn.addEventListener('click', closeImagePreview);
          document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && !lightbox.hidden) closeImagePreview();
          });
        })();
        """;
}
