using System.IO;

namespace Athlon.Agent.App.Services;

/// <summary>WebChatView 通过虚拟主机加载的 Chat 静态资源目录名。</summary>
internal static class ChatMarkdownAssets
{
    public const string VirtualHost = "athlon.chat.assets";

    public static string VirtualBaseUrl => $"https://{VirtualHost}/";

    public static string AssetsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Chat");
}
