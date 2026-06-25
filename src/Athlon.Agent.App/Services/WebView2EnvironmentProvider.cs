using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services;

/// <summary>Creates and caches a shared <see cref="CoreWebView2Environment"/> for all WebView2 controls.</summary>
public sealed class WebView2EnvironmentProvider
{
    private const string BundledUserDataFolderName = "bundled";
    private const string EvergreenUserDataFolderName = "evergreen";

    private readonly IAppPathProvider _paths;
    private readonly IAppLogger _logger;
    private readonly object _lock = new();
    private Task<CoreWebView2Environment>? _environmentTask;

    public WebView2EnvironmentProvider(IAppPathProvider paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger.ForContext(nameof(WebView2EnvironmentProvider));
    }

    public Task<CoreWebView2Environment> GetEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _environmentTask ??= CreateEnvironmentAsync(cancellationToken);
            return _environmentTask;
        }
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync(CancellationToken cancellationToken)
    {
        var browserFolder = WebView2RuntimeLocator.TryResolveBundledFolder();
        if (browserFolder is not null)
        {
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
                    ex,
                    "Bundled WebView2 runtime failed at {Folder}; falling back to Evergreen",
                    browserFolder);
                App.StartupTrace($"WebView2 bundled runtime failed ({ex.Message}); falling back to Evergreen");
            }
        }
        else
        {
            _logger.Warning("Bundled WebView2 runtime not found; using Evergreen");
            App.StartupTrace("WebView2 bundled runtime missing; using Evergreen");
        }

        var evergreenUserData = EnsureUserDataFolder(EvergreenUserDataFolderName);
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, evergreenUserData)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            App.StartupTrace("WebView2 using Evergreen runtime");
            return environment;
        }
        catch (Exception ex)
        {
            const string message = "WebView2 初始化失败：捆绑 Runtime 与系统 Evergreen Runtime 均不可用。";
            _logger.Error(ex, "{Message}", message);
            App.StartupTrace($"WebView2 initialization failed: {ex.Message}");
            throw new InvalidOperationException(message, ex);
        }
    }

    private string EnsureUserDataFolder(string modeFolderName)
    {
        var userDataFolder = Path.Combine(_paths.RootPath, "webview2", modeFolderName);
        Directory.CreateDirectory(userDataFolder);
        return userDataFolder;
    }
}
