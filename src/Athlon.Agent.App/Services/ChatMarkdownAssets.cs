using System.IO;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.Services;

/// <summary>WebChatView 通过虚拟主机加载的 Chat 静态资源目录名。</summary>
internal static class ChatMarkdownAssets
{
    public const string VirtualHost = "athlon.chat.assets";

    public static string VirtualBaseUrl => $"https://{VirtualHost}/";

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
