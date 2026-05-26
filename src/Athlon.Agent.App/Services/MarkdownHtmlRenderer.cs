using Markdig;

namespace Athlon.Agent.App.Services;

public static class MarkdownHtmlRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string BuildDocument(string markdown, bool assistantTone = true)
    {
        var body = string.IsNullOrWhiteSpace(markdown)
            ? string.Empty
            : Markdown.ToHtml(markdown, Pipeline);

        var textColor = assistantTone ? "#F4F4F5" : "#EFF6FF";
        var linkColor = assistantTone ? "#93C5FD" : "#DBEAFE";

        return $"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="UTF-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <style>{BuildStyles(textColor, linkColor)}</style>
            </head>
            <body>
              <div class="md-root">{body}</div>
              <script>{BuildScript()}</script>
            </body>
            </html>
            """;
    }

    public static string BuildPreviewDocument(string rawHtml)
    {
        if (HtmlDocumentRegex.IsMatch(rawHtml))
        {
            return rawHtml;
        }

        return """
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="UTF-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>HTML 预览</title>
              <style>
                body { margin: 0; min-height: 100vh; background: #ffffff; }
              </style>
            </head>
            <body>
            """ + rawHtml + """
            </body>
            </html>
            """;
    }

    private static readonly System.Text.RegularExpressions.Regex HtmlDocumentRegex =
        new(@"<html[\s>]|<!doctype", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string BuildStyles(string textColor, string linkColor) =>
        BaseStyles.Replace("__TEXT_COLOR__", textColor, StringComparison.Ordinal)
            .Replace("__LINK_COLOR__", linkColor, StringComparison.Ordinal);

    private const string BaseStyles = """
        * { box-sizing: border-box; }
        html, body {
          margin: 0;
          padding: 0;
          background: transparent;
          color: __TEXT_COLOR__;
          font-family: "Segoe UI", "Inter", "PingFang SC", "Hiragino Sans GB", sans-serif;
          font-size: 14px;
          line-height: 1.6;
          overflow-x: hidden;
          overflow-y: auto;
        }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: transparent; }
        ::-webkit-scrollbar-thumb {
          border: 2px solid transparent;
          border-radius: 9999px;
          background: rgba(148, 163, 184, 0.55);
          background-clip: padding-box;
        }
        .md-root {
          width: 100%;
          max-width: 100%;
          word-wrap: break-word;
          overflow-wrap: anywhere;
        }
        .md-root p { margin: 0 0 12px; }
        .md-root p:last-child { margin-bottom: 0; }
        .md-root ul, .md-root ol { margin: 0 0 12px; padding-left: 24px; }
        .md-root li { margin-bottom: 6px; }
        .md-root h1, .md-root h2, .md-root h3, .md-root h4 {
          margin: 16px 0 10px;
          font-weight: 600;
          line-height: 1.35;
        }
        .md-root h1 { font-size: 1.5em; }
        .md-root h2 { font-size: 1.3em; }
        .md-root h3 { font-size: 1.15em; }
        .md-root a { color: __LINK_COLOR__; text-decoration: underline; text-underline-offset: 2px; }
        .md-root code:not(pre code) {
          border-radius: 6px;
          background: #27272A;
          padding: 2px 6px;
          font-family: Consolas, "Cascadia Code", monospace;
          font-size: 0.9em;
        }
        .md-root table {
          width: 100%;
          border-collapse: collapse;
          margin: 16px 0;
          font-size: 13px;
        }
        .md-root th, .md-root td {
          border: 1px solid #3F3F46;
          padding: 8px 12px;
          text-align: left;
          vertical-align: top;
        }
        .md-root th { background: #27272A; font-weight: 600; }
        .md-root blockquote {
          margin: 12px 0;
          padding: 8px 14px;
          border-left: 3px solid #3B82F6;
          color: #A1A1AA;
          background: rgba(39, 39, 42, 0.5);
          border-radius: 0 8px 8px 0;
        }
        .code-block {
          margin: 16px 0;
          border: 1px solid #1E293B;
          border-radius: 16px;
          overflow: hidden;
          background: #020617;
        }
        .code-block-header {
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 8px;
          padding: 8px 16px;
          border-bottom: 1px solid #1E293B;
          font-size: 12px;
          color: #CBD5E1;
        }
        .code-block-actions { display: flex; gap: 8px; }
        .code-btn {
          border: 1px solid #334155;
          border-radius: 6px;
          background: transparent;
          color: #CBD5E1;
          padding: 4px 8px;
          font-size: 12px;
          cursor: pointer;
        }
        .code-btn:hover { border-color: #64748B; color: #fff; }
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
          color: #F1F5F9;
        }
        .code-block pre code {
          font-family: Consolas, "Cascadia Code", monospace;
          white-space: pre;
        }
        """;

    private static string BuildScript() => """
        (function () {
          function post(payload) {
            if (window.chrome && window.chrome.webview) {
              // 必须传对象，不能 JSON.stringify；否则 C# 端 WebMessageAsJson 无法解析
              window.chrome.webview.postMessage(payload);
            }
          }

          function enhanceCodeBlocks() {
            document.querySelectorAll('.md-root pre').forEach(function (pre, index) {
              if (pre.closest('.code-block')) return;
              var code = pre.querySelector('code');
              if (!code) return;

              var raw = code.textContent || '';
              var className = code.className || '';
              var match = className.match(/language-([\w-]+)/i);
              var language = match ? match[1] : '代码';
              var canPreview = language.toLowerCase() === 'html';

              var wrapper = document.createElement('div');
              wrapper.className = 'code-block';

              var header = document.createElement('div');
              header.className = 'code-block-header';

              var label = document.createElement('span');
              label.textContent = language;

              var actions = document.createElement('div');
              actions.className = 'code-block-actions';

              if (canPreview) {
                var previewBtn = document.createElement('button');
                previewBtn.type = 'button';
                previewBtn.className = 'code-btn';
                previewBtn.textContent = '预览';
                previewBtn.addEventListener('click', function () {
                  post({ type: 'preview', html: raw });
                });
                actions.appendChild(previewBtn);
              }

              var copyBtn = document.createElement('button');
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

          var heightTimer = null;
          function reportHeight() {
            if (heightTimer) clearTimeout(heightTimer);
            heightTimer = setTimeout(function () {
              var height = Math.max(
                document.body.scrollHeight,
                document.documentElement.scrollHeight
              );
              post({ type: 'height', value: height });
            }, 32);
          }

          enhanceCodeBlocks();
          reportHeight();
          if (typeof ResizeObserver !== 'undefined') {
            new ResizeObserver(reportHeight).observe(document.body);
          }
        })();
        """;
}
