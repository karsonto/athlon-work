using System.Net;
using System.Text;

namespace Athlon.Agent.App.Services;

public static class MermaidPreviewHtmlBuilder
{
    public const string VirtualHostName = "athlon.assets";
    public const string MermaidFolderName = "Mermaid";

    public static string AssetsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", MermaidFolderName);

    public static string MermaidScriptPath =>
        Path.Combine(AssetsDirectory, "mermaid.min.js");

    public static bool IsBundledRuntimeAvailable() =>
        File.Exists(MermaidScriptPath);

    public static string BuildDocument(IReadOnlyList<string> diagrams)
    {
        if (diagrams.Count == 0)
        {
            throw new ArgumentException("At least one Mermaid diagram is required.", nameof(diagrams));
        }

        var body = new StringBuilder();
        for (var i = 0; i < diagrams.Count; i++)
        {
            body.AppendLine("<section class=\"diagram-card\">");
            if (diagrams.Count > 1)
            {
                body.Append("<h2 class=\"diagram-title\">图表 ")
                    .Append(i + 1)
                    .AppendLine("</h2>");
            }

            body.Append("<pre class=\"mermaid\">")
                .Append(WebUtility.HtmlEncode(diagrams[i]))
                .AppendLine("</pre>");
            body.AppendLine("</section>");
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="UTF-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>Mermaid 预览</title>
              <style>
                * { box-sizing: border-box; }
                html, body {
                  margin: 0;
                  padding: 24px;
                  min-height: 100%;
                  background: #101012;
                  color: #F4F4F5;
                  font-family: "Segoe UI", "PingFang SC", sans-serif;
                }
                h1 {
                  margin: 0 0 20px;
                  font-size: 20px;
                  font-weight: 600;
                }
                .diagram-card {
                  margin: 0 0 24px;
                  padding: 16px;
                  border: 1px solid #3F3F46;
                  border-radius: 16px;
                  background: #18181B;
                  overflow-x: auto;
                }
                .diagram-title {
                  margin: 0 0 12px;
                  font-size: 14px;
                  color: #A1A1AA;
                  font-weight: 600;
                }
                .mermaid { margin: 0; background: transparent; }
                #status { margin-top: 12px; color: #A1A1AA; font-size: 13px; }
                #error {
                  display: none;
                  margin-top: 16px;
                  padding: 12px 14px;
                  border-radius: 8px;
                  border: 1px solid #E11D48;
                  background: #2A1418;
                  color: #FDA4AF;
                  white-space: pre-wrap;
                  font-family: Consolas, monospace;
                  font-size: 13px;
                }
              </style>
              <script src="https://{{VirtualHostName}}/mermaid.min.js"></script>
            </head>
            <body>
              <h1>Mermaid 图表预览</h1>
              <div id="status">正在渲染…</div>
              <div id="error"></div>
              {{body}}
              <script>
                (async function () {
                  var status = document.getElementById('status');
                  var errorBox = document.getElementById('error');
                  function fail(message) {
                    status.textContent = '渲染失败';
                    errorBox.style.display = 'block';
                    errorBox.textContent = message;
                  }

                  if (typeof mermaid === 'undefined') {
                    fail('未加载 Mermaid 脚本。请确认安装包内包含 Assets/Mermaid/mermaid.min.js。');
                    return;
                  }

                  try {
                    mermaid.initialize({
                      startOnLoad: false,
                      theme: 'dark',
                      securityLevel: 'strict',
                      fontFamily: 'Segoe UI, PingFang SC, sans-serif'
                    });
                    await mermaid.run({ querySelector: '.mermaid' });
                    status.textContent = '渲染完成';
                  } catch (err) {
                    fail(err && (err.message || String(err)) || '未知错误');
                  }
                })();
              </script>
            </body>
            </html>
            """;
    }
}
