using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.Services;

/// <summary>Builds a full HTML document for Session Plan preview (Markdown + inline Mermaid).</summary>
public static partial class PlanDocumentHtmlBuilder
{
    [GeneratedRegex(@"```\s*mermaid\s*\r?\n([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex MermaidFenceRegex();

    public static string BuildDocument(string? title, string? overview, string? body)
    {
        var markdown = ComposeMarkdown(title, overview, body);
        var palette = ThemeHtmlStyles.GetMermaidPalette();
        var mdPalette = ThemeHtmlStyles.GetMarkdownPalette(assistantTone: true);

        var mermaidBlocks = new List<string>();
        var withPlaceholders = MermaidFenceRegex().Replace(markdown, match =>
        {
            var index = mermaidBlocks.Count;
            mermaidBlocks.Add(match.Groups[1].Value.Trim());
            return $"\n\nPLAN_MERMAID_{index}\n\n";
        });

        var htmlBody = MarkdownHtmlRenderer.ToHtmlFragment(withPlaceholders);
        for (var i = 0; i < mermaidBlocks.Count; i++)
        {
            var card = $"""
                <section class="diagram-card"><pre class="mermaid">{WebUtility.HtmlEncode(mermaidBlocks[i])}</pre></section>
                """;
            htmlBody = htmlBody.Replace(
                $"<p>PLAN_MERMAID_{i}</p>",
                card,
                StringComparison.Ordinal);
            htmlBody = htmlBody.Replace(
                $"PLAN_MERMAID_{i}",
                card,
                StringComparison.Ordinal);
        }

        var mermaidScript = MermaidPreviewHtmlBuilder.IsBundledRuntimeAvailable()
            ? $"<script src=\"https://{MermaidPreviewHtmlBuilder.VirtualHostName}/mermaid.min.js\"></script>"
            : "";
        var missingNote = MermaidPreviewHtmlBuilder.IsBundledRuntimeAvailable()
            ? ""
            : "<p class=\"warn\">Mermaid assets missing — diagrams shown as source only.</p>";

        return $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="UTF-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>{{WebUtility.HtmlEncode(title ?? "Plan")}}</title>
              <style>
                * { box-sizing: border-box; }
                html, body {
                  margin: 0;
                  padding: 16px;
                  min-height: 100%;
                  background: {{palette.PageBackground}};
                  color: {{mdPalette.TextColor}};
                  font-family: "Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", "PingFang SC", system-ui, sans-serif;
                  font-size: 13px;
                  line-height: 1.55;
                }
                .md-root h1 { font-size: 1.35em; margin: 0 0 10px; font-weight: 600; }
                .md-root h2 { font-size: 1.15em; margin: 18px 0 8px; font-weight: 600; }
                .md-root h3 { font-size: 1.05em; margin: 14px 0 6px; font-weight: 600; }
                .md-root p { margin: 0 0 10px; }
                .md-root ul, .md-root ol { margin: 0 0 10px; padding-left: 22px; }
                .md-root code:not(pre code) {
                  background: {{mdPalette.InlineCodeBackground}};
                  border-radius: 4px;
                  padding: 1px 5px;
                  font-family: Consolas, monospace;
                  font-size: 0.9em;
                }
                .md-root pre {
                  margin: 10px 0;
                  padding: 10px 12px;
                  overflow-x: auto;
                  border-radius: 8px;
                  background: {{mdPalette.CodeBlockBackground}};
                  border: 1px solid {{mdPalette.CodeBlockBorder}};
                }
                .md-root blockquote {
                  margin: 10px 0;
                  padding: 8px 12px;
                  border-left: 3px solid {{palette.CardBorder}};
                  color: {{palette.SubtleText}};
                  background: {{palette.CardBackground}};
                  border-radius: 0 8px 8px 0;
                }
                .diagram-card {
                  margin: 12px 0 16px;
                  padding: 12px;
                  border: 1px solid {{palette.CardBorder}};
                  border-radius: 12px;
                  background: {{palette.CardBackground}};
                  overflow-x: auto;
                }
                .mermaid { margin: 0; background: transparent; }
                .warn { color: {{palette.SubtleText}}; font-size: 12px; }
              </style>
              {{mermaidScript}}
            </head>
            <body>
              {{missingNote}}
              <div class="md-root">{{htmlBody}}</div>
              <script>
                (async function () {
                  if (typeof mermaid === 'undefined') return;
                  try {
                    mermaid.initialize({
                      startOnLoad: false,
                      theme: '{{palette.MermaidTheme}}',
                      securityLevel: 'strict',
                      fontFamily: 'Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, PingFang SC, system-ui, sans-serif'
                    });
                    await mermaid.run({ querySelector: '.mermaid' });
                  } catch (e) {}
                })();
              </script>
            </body>
            </html>
            """;
    }

    public static string ComposeMarkdown(string? title, string? overview, string? body)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append("# ").AppendLine(title.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(overview))
        {
            sb.Append("> ").AppendLine(overview.Trim().Replace("\n", "\n> ", StringComparison.Ordinal));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.AppendLine(body.Trim());
        }

        return sb.ToString();
    }
}
