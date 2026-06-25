using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services;

/// <summary>Creates and caches a shared <see cref="CoreWebView2Environment"/> for all WebView2 controls.</summary>
public sealed class WebView2EnvironmentProvider
{
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
        var userDataFolder = Path.Combine(_paths.RootPath, "webview2");
        Directory.CreateDirectory(userDataFolder);

        var browserFolder = WebView2RuntimeLocator.TryResolveBundledFolder();
        if (browserFolder is not null)
        {
            var bundledVersion = WebView2RuntimeLocator.TryReadBundledVersion();
            _logger.Information(
                "WebView2 using bundled fixed runtime {Version} at {Folder}",
                bundledVersion ?? "unknown",
                browserFolder);
            App.StartupTrace($"WebView2 using bundled fixed runtime at {browserFolder}");
            return await CoreWebView2Environment.CreateAsync(browserFolder, userDataFolder)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

#if DEBUG
        _logger.Warning("Bundled WebView2 runtime not found; using Evergreen fallback (Debug only)");
        App.StartupTrace("WebView2 bundled runtime missing; using Evergreen fallback (Debug only)");
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
#else
        const string message =
            "缺少捆绑的 WebView2 Runtime。请使用正式发布包，或在构建前运行 tools/fetch-webview2-fixed-runtime.ps1。";
        _logger.Error(new InvalidOperationException(message), "{Message}", message);
        App.StartupTrace($"WebView2 initialization failed: {message}");
        throw new InvalidOperationException(message);
#endif
    }
}
