namespace Athlon.Agent.App.Services;

/// <summary>Locates the bundled WebView2 Fixed Version runtime shipped with the app.</summary>
internal static class WebView2RuntimeLocator
{
    private const string BundledRelativePath = "runtimes/webview2/x64";

    public static string? TryResolveBundledFolder() =>
        TryResolveBundledFolder(AppContext.BaseDirectory);

    internal static string? TryResolveBundledFolder(string baseDirectory)
    {
        var direct = Path.Combine(baseDirectory, BundledRelativePath);
        if (File.Exists(Path.Combine(direct, "msedgewebview2.exe")))
        {
            return direct;
        }

        return FindMsEdgeWebView2Folder(baseDirectory);
    }

    public static string? TryReadBundledVersion()
    {
        var versionFile = Path.Combine(AppContext.BaseDirectory, "runtimes", "webview2", "VERSION");
        if (!File.Exists(versionFile))
        {
            return null;
        }

        var version = File.ReadAllText(versionFile).Trim();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static string? FindMsEdgeWebView2Folder(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(directory, "msedgewebview2.exe")))
                {
                    return directory;
                }

                foreach (var nested in Directory.EnumerateDirectories(directory))
                {
                    if (File.Exists(Path.Combine(nested, "msedgewebview2.exe")))
                    {
                        return nested;
                    }
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
