using System.Net;
using System.Text;
using System.Text.Json;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>构建 WebChatView 外壳 HTML 与 AG-UI 风格的事件驱动时间线脚本。</summary>
public sealed class ChatHtmlBuilder
{
    private const string WelcomeDescription =
        "Athlon Agent 可以帮您分析代码、生成原型、优化设计，或执行任何开发任务。";

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
            "<div id=\"messages\"></div></div>" +
            $"<script src=\"{assets}marked.min.js\"></script>" +
            $"<script src=\"{assets}highlight.min.js\"></script>" +
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

    private static string BuildEmptyStateHtml(string? ssoDisplayName)
    {
        var title = string.IsNullOrWhiteSpace(ssoDisplayName)
            ? "开始新的对话"
            : $"你好，{ssoDisplayName.Trim()}";
        return
            "<div id=\"empty-state\" class=\"empty-state\">" +
            "<div class=\"empty-state-icon\">💬</div>" +
            $"<h2 class=\"empty-state-title\">{WebUtility.HtmlEncode(title)}</h2>" +
            $"<p class=\"empty-state-description\">{WebUtility.HtmlEncode(WelcomeDescription)}</p>" +
            "</div>";
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
          font-family: "Inter", "Segoe UI", "PingFang SC", "Hiragino Sans GB", sans-serif;
          font-size: 14px;
          line-height: 1.5;
          color: var(--assistant-text);
          background: var(--chat-bg);
          height: 100%;
          overflow: hidden;
          -webkit-font-smoothing: antialiased;
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
        .empty-state {
          position: absolute;
          inset: 0;
          display: flex;
          flex-direction: column;
          align-items: center;
          justify-content: center;
          padding: 24px;
          text-align: center;
          pointer-events: none;
          z-index: 1;
        }
        .empty-state-icon {
          font-size: 48px;
          line-height: 1;
          opacity: 0.5;
          margin-bottom: 24px;
        }
        .empty-state-title {
          font-size: 24px;
          font-weight: 600;
          color: var(--assistant-text);
          margin-bottom: 12px;
        }
        .empty-state-description {
          font-size: 14px;
          color: var(--subtle-text);
          max-width: 400px;
          line-height: 1.6;
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
          reasoningFinalizedMs: {}
        };

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

        function renderMarkdownText(text) {
          const source = text == null ? '' : String(text);
          if (!source.trim()) return '';
          if (typeof marked !== 'undefined') {
            try {
              if (typeof marked.setOptions === 'function') {
                marked.setOptions({ gfm: true, breaks: true });
              }
              if (typeof marked.parse === 'function') {
                return marked.parse(source, { async: false });
              }
              if (typeof marked === 'function') {
                return marked(source);
              }
            } catch (e) {
              console.warn('marked.parse failed', e);
            }
          }
          return '<pre>' + escapeHtml(source) + '</pre>';
        }

        function resolveRenderedHtml(event, fallbackText) {
          const html = resolveEventHtml(event);
          if (html) return html;
          const markdown = resolveEventMarkdown(event) || fallbackText || '';
          return renderMarkdownText(markdown);
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

        function scrollToBottom() {
          const scroller = getChatScroller();
          if (!scroller) return;
          scroller.scrollTop = scroller.scrollHeight;
        }

        function updateEmptyStateVisibility() {
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

        function applyMarkdownHtml(node, html) {
          if (!node) return;
          node.classList.add('md-root');
          node.innerHTML = html || '';
          enhanceCodeBlocks(node);
        }

        function applyAssistantHtml(messageId, html, createIfMissing) {
          let row = findAssistantBubbleRow(messageId);
          if (!row && createIfMissing) {
            row = createAssistantRow(messageId);
            document.getElementById('messages').appendChild(row);
            state.assistantStarted[messageId] = true;
            state.currentAssistantEl = row;
          }
          applyMarkdownHtml(findAssistantContentNode(messageId), html);
          updateEmptyStateVisibility();
          scrollToBottom();
        }

        function finalizeAssistantMarkdown(messageId) {
          const node = findAssistantContentNode(messageId);
          if (!node) return;
          const raw = node.textContent || '';
          if (!raw.trim()) return;
          applyMarkdownHtml(node, renderMarkdownText(raw));
          scrollToBottom();
        }

        function enhanceCodeBlocks(root) {
          const scope = root || document;
          scope.querySelectorAll('.md-root pre').forEach(function (pre, index) {
            if (pre.closest('.code-block')) return;
            const code = pre.querySelector('code');
            if (!code) return;

            if (typeof hljs !== 'undefined' && !code.dataset.hljsDone) {
              try {
                hljs.highlightElement(code);
                code.dataset.hljsDone = '1';
              } catch (e) {}
            }

            const raw = code.textContent || '';
            const className = code.className || '';
            const match = className.match(/language-([\w#+-]+)/i);
            const language = match ? match[1] : '代码';

            const wrapper = document.createElement('div');
            wrapper.className = 'code-block';

            const header = document.createElement('div');
            header.className = 'code-block-header';

            const label = document.createElement('span');
            label.textContent = language;

            const actions = document.createElement('div');
            actions.className = 'code-block-actions';

            const copyBtn = document.createElement('button');
            copyBtn.type = 'button';
            copyBtn.className = 'code-btn';
            copyBtn.textContent = '复制';
            copyBtn.addEventListener('click', function () {
              post({ type: 'copy', text: raw, blockId: String(index) });
              copyBtn.textContent = '已复制';
              copyBtn.classList.add('copied');
              setTimeout(function () {
                copyBtn.textContent = '复制';
                copyBtn.classList.remove('copied');
              }, 1600);
            });
            actions.appendChild(copyBtn);

            header.appendChild(label);
            header.appendChild(actions);

            pre.parentNode.insertBefore(wrapper, pre);
            wrapper.appendChild(header);
            wrapper.appendChild(pre);
          });
        }

        function resetTimeline() {
          const root = document.getElementById('messages');
          root.innerHTML = '';
          state.currentAssistantEl = null;
          state.currentReasoningEl = null;
          state.assistantStarted = {};
          state.reasoningStarted = {};
          state.reasoningStartAt = {};
          state.reasoningFinalizedMs = {};
          state.toolCalls.clear();
        }

        function formatReasoningSeconds(ms) {
          return Math.max(1, Math.round(ms / 1000)) + '秒';
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
            setReasoningLabel(messageId, '正在思考');
            return;
          }
          setReasoningLabel(
            messageId,
            '正在思考 (' + formatReasoningSeconds(getReasoningElapsedMs(messageId)) + ')');
        }

        function finalizeReasoningLabel(messageId) {
          if (!messageId) return;
          const row = findReasoningRow(messageId);
          if (!row) return;
          if (!state.trackReasoningDuration) {
            setReasoningLabelOnRow(row, '已思考');
            delete state.reasoningStartAt[messageId];
            delete state.reasoningFinalizedMs[messageId];
            return;
          }
          if (state.reasoningFinalizedMs[messageId] !== undefined) {
            return;
          }
          const ms = getReasoningElapsedMs(messageId);
          state.reasoningFinalizedMs[messageId] = ms;
          setReasoningLabelOnRow(row, '已思考 (' + formatReasoningSeconds(ms) + ')');
          delete state.reasoningStartAt[messageId];
        }

        function createUserRow(content) {
          const row = document.createElement('div');
          row.className = 'message-row user';
          row.innerHTML =
            '<div class="bubble">' +
              '<div class="message-content user-text">' + escapeHtml(content) + '</div>' +
            '</div>';
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
              '<summary><span class="reasoning-chevron">›</span><span class="reasoning-label">正在思考</span></summary>' +
              '<div class="reasoning-content message-content"></div>' +
            '</details>';
          return row;
        }

        function appendMessage(role, content, append) {
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
            document.getElementById('messages').appendChild(createUserRow(content));
          } else if (role === 'assistant') {
            const row = createAssistantRow('');
            row.querySelector('.message-content').textContent = content;
            document.getElementById('messages').appendChild(row);
            state.currentAssistantEl = row;
          } else if (role === 'reasoning') {
            const row = createReasoningRow('');
            row.querySelector('.reasoning-content').textContent = content;
            document.getElementById('messages').appendChild(row);
            state.currentReasoningEl = row;
          }
          updateEmptyStateVisibility();
          scrollToBottom();
        }

        function ensureAssistantBubble(messageId) {
          if (state.currentAssistantEl && state.assistantStarted[messageId]) return;
          const row = createAssistantRow(messageId);
          document.getElementById('messages').appendChild(row);
          state.currentAssistantEl = row;
          state.assistantStarted[messageId] = true;
          updateEmptyStateVisibility();
        }

        function ensureReasoningBubble(messageId) {
          if (state.currentReasoningEl && state.reasoningStarted[messageId]) return;
          const row = createReasoningRow(messageId);
          document.getElementById('messages').appendChild(row);
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
          document.getElementById('messages').appendChild(row);
          state.toolCalls.set(toolCallId, details);
          updateEmptyStateVisibility();
          scrollToBottom();
        }

        function getToolCard(toolCallId) {
          return state.toolCalls.get(toolCallId) || document.querySelector('[data-tool-call-id="' + toolCallId + '"]');
        }

        function handleEvent(event) {
          if (!event || !event.type) return;
          switch (event.type) {
            case 'RESET_TIMELINE':
              resetTimeline();
              updateEmptyStateVisibility();
              break;
            case 'USER_MESSAGE':
              appendMessage('user', event.content || '');
              break;
            case 'RUN_STARTED':
              state.currentAssistantEl = null;
              state.currentReasoningEl = null;
              break;
            case 'REASONING_MESSAGE_START':
              state.currentReasoningEl = null;
              state.reasoningStarted[event.messageId] = false;
              delete state.reasoningFinalizedMs[event.messageId];
              if (state.trackReasoningDuration) {
                state.reasoningStartAt[event.messageId] = performance.now();
              }
              break;
            case 'REASONING_MESSAGE_CONTENT':
              if (!state.reasoningStarted[event.messageId]) ensureReasoningBubble(event.messageId);
              if (state.trackReasoningDuration && !state.reasoningStartAt[event.messageId]) {
                state.reasoningStartAt[event.messageId] = performance.now();
              }
              appendMessage('reasoning', event.delta || '', true);
              updateReasoningThinkingLabel(event.messageId);
              break;
            case 'REASONING_MESSAGE_END':
              finalizeReasoningLabel(event.messageId);
              state.currentReasoningEl = null;
              break;
            case 'TEXT_MESSAGE_START':
              state.currentAssistantEl = null;
              state.assistantStarted[event.messageId] = false;
              break;
            case 'TEXT_MESSAGE_CONTENT':
              finalizeReasoningLabel(event.messageId);
              if (!state.assistantStarted[event.messageId]) ensureAssistantBubble(event.messageId);
              appendMessage('assistant', event.delta || '', true);
              break;
            case 'TEXT_MESSAGE_END':
              finalizeAssistantMarkdown(event.messageId);
              state.currentAssistantEl = null;
              break;
            case 'STATIC_ASSISTANT_HTML':
              applyAssistantHtml(
                event.messageId,
                resolveRenderedHtml(event),
                event.createIfMissing !== false);
              state.currentAssistantEl = null;
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
              if (card) {
                const badge = card.querySelector('.tool-status');
                if (badge) { badge.textContent = 'done'; badge.className = 'tool-status success'; }
              }
              break;
            }
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
              const badge = card.querySelector('.tool-status');
              if (badge) { badge.textContent = 'success'; badge.className = 'tool-status success'; }
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
          state.trackReasoningDuration = false;
          resetTimeline();
          for (const raw of events) {
            try {
              const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
              handleEvent(event);
            } catch (e) { console.warn('replayEvents parse failed', e); }
          }
          updateEmptyStateVisibility();
          scrollToBottom();
          state.trackReasoningDuration = true;
        }
        """;
}
