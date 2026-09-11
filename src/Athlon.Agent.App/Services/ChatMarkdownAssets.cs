using System.IO;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.Services;

/// <summary>WebChatView 通过虚拟主机加载的 Chat 静态资源目录名。</summary>
internal static class ChatMarkdownAssets
{
    public const string VirtualHost = "athlon.chat.assets";

    public static string VirtualBaseUrl => $"https://{VirtualHost}/";

    /// <summary>
    /// Separate host for the bundled Mermaid runtime. The script is ~2.5 MB, so the timeline
    /// lazy-loads it from here only when a ```mermaid block actually shows up.
    /// </summary>
    public const string MermaidVirtualHost = "athlon.chat.mermaid";

    public const string MermaidScriptFileName = "mermaid.min.js";

    public static string MermaidVirtualBaseUrl => $"https://{MermaidVirtualHost}/";

    public static string MermaidAssetsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", MermaidPreviewHtmlBuilder.MermaidFolderName);

    public static string AssetsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Chat");

    /// <summary>Query stamp so WebView2 does not keep stale chat-shell / timeline assets across rebuilds.</summary>
    public static string AssetCacheQuery
    {
        get
        {
            try
            {
                var css = Path.Combine(AssetsDirectory, "chat-shell.css");
                var js = Path.Combine(AssetsDirectory, "chat-timeline.js");
                var stamp = 0L;
                if (File.Exists(css))
                {
                    stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(css).Ticks);
                }

                if (File.Exists(js))
                {
                    stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(js).Ticks);
                }

                var mermaid = Path.Combine(MermaidAssetsDirectory, MermaidScriptFileName);
                if (File.Exists(mermaid))
                {
                    stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(mermaid).Ticks);
                }

                return stamp > 0 ? $"?v={stamp:x}" : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public static string GetHighlightStylesheet() =>
        AppThemeManager.CurrentKind == AppThemeKind.Light
            ? "github.min.css"
            : "github-dark.min.css";
}
