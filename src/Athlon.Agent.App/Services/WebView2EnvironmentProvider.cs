using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services;

/// <summary>Creates an optional bundled <see cref="CoreWebView2Environment"/> for Windows 10.</summary>
public sealed class WebView2EnvironmentProvider
{
    private const string BundledUserDataFolderName = "bundled";

    private readonly IAppPathProvider _paths;
    private readonly IAppLogger _logger;
    private readonly object _lock = new();
    private Task<CoreWebView2Environment?>? _bundledEnvironmentTask;

    public WebView2EnvironmentProvider(IAppPathProvider paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger.ForContext(nameof(WebView2EnvironmentProvider));
    }

    /// <summary>
    /// Returns a bundled fixed environment on Windows 10 when available.
    /// Returns <see langword="null"/> on Windows 11 or when bundled files are missing/failed.
    /// </summary>
    public Task<CoreWebView2Environment?> TryGetBundledEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        if (!WebView2RuntimePolicy.ShouldUseBundledRuntime())
        {
            return Task.FromResult<CoreWebView2Environment?>(null);
        }

        lock (_lock)
        {
            _bundledEnvironmentTask ??= CreateBundledEnvironmentAsync(cancellationToken);
            return _bundledEnvironmentTask;
        }
    }

    private async Task<CoreWebView2Environment?> CreateBundledEnvironmentAsync(CancellationToken cancellationToken)
    {
        var browserFolder = WebView2RuntimeLocator.TryResolveBundledFolder();
        if (browserFolder is null)
        {
            _logger.Warning("Bundled WebView2 runtime not found on Windows 10");
            App.StartupTrace("WebView2 bundled runtime missing on Windows 10");
            return null;
        }

        var bundledVersion = WebView2RuntimeLocator.TryReadBundledVersion();
        var bundledUserData = EnsureUserDataFolder(BundledUserDataFolderName);
        try
        {
            _logger.Information(
                "WebView2 trying bundled fixed runtime {Version} at {Folder}",
                bundledVersion ?? "unknown",
                browserFolder);
            App.StartupTrace($"WebView2 trying bundled fixed runtime at {browserFolder}");
            var environment = await CoreWebView2Environment.CreateAsync(browserFolder, bundledUserData)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            App.StartupTrace("WebView2 using bundled fixed runtime");
            return environment;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Bundled WebView2 runtime failed at {Folder}: {Error}",
                browserFolder,
                ex.Message);
            App.StartupTrace($"WebView2 bundled runtime failed ({ex.Message})");
            return null;
        }
    }

    private string EnsureUserDataFolder(string modeFolderName)
    {
        var userDataFolder = Path.Combine(_paths.RootPath, "webview2", modeFolderName);
        Directory.CreateDirectory(userDataFolder);
        return userDataFolder;
    }
}
