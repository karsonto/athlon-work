using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services;

/// <summary>Creates an optional bundled <see cref="CoreWebView2Environment"/> for Windows 10.</summary>
public sealed class WebView2EnvironmentProvider
{
    private const string BundledUserDataFolderName = "bundled";
    private const string BrowserUserDataFolderName = "browser";

    private readonly IAppPathProvider _paths;
    private readonly IAppLogger _logger;
    private readonly IRuntimeDiagnosticEventSink? _runtimeDiagnosticEventSink;
    private readonly object _lock = new();
    private Task<CoreWebView2Environment?>? _bundledEnvironmentTask;
    private Task<CoreWebView2Environment>? _browserEnvironmentTask;

    public WebView2EnvironmentProvider(
        IAppPathProvider paths,
        IAppLogger logger,
        IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null)
    {
        _paths = paths;
        _logger = logger.ForContext(nameof(WebView2EnvironmentProvider));
        _runtimeDiagnosticEventSink = runtimeDiagnosticEventSink;
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

    /// <summary>
    /// Shared environment for workspace Browser tabs. Cookies and site storage stay in
    /// <c>webview2/browser</c> under the app data root, independent of the executable path.
    /// </summary>
    public Task<CoreWebView2Environment> GetBrowserEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _browserEnvironmentTask ??= CreateBrowserEnvironmentAsync(cancellationToken);
            return _browserEnvironmentTask;
        }
    }

    internal static string BrowserUserDataFolder(string rootPath) =>
        UserDataFolder(rootPath, BrowserUserDataFolderName);

    internal static string BundledUserDataFolder(string rootPath) =>
        UserDataFolder(rootPath, BundledUserDataFolderName);

    private static string UserDataFolder(string rootPath, string modeFolderName) =>
        Path.Combine(rootPath, "webview2", modeFolderName);

    private async Task<CoreWebView2Environment> CreateBrowserEnvironmentAsync(CancellationToken cancellationToken)
    {
        var userData = EnsureUserDataFolder(BrowserUserDataFolderName);
        var bundledFolder = WebView2RuntimePolicy.ShouldUseBundledRuntime()
            ? WebView2RuntimeLocator.TryResolveBundledFolder()
            : null;
        if (!string.IsNullOrWhiteSpace(bundledFolder))
        {
            try
            {
                _logger.Information(
                    "WebView2 browser profile using bundled runtime at {Folder}",
                    bundledFolder);
                return await CoreWebView2Environment.CreateAsync(bundledFolder, userData)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "Bundled WebView2 browser profile failed at {Folder}: {Error}",
                    bundledFolder,
                    ex.Message);
                App.StartupTrace($"WebView2 browser bundled runtime failed ({ex.Message})");
            }
        }

        _logger.Information("WebView2 browser profile using Evergreen runtime at {UserData}", userData);
        return await CoreWebView2Environment.CreateAsync(null, userData)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CoreWebView2Environment?> CreateBundledEnvironmentAsync(CancellationToken cancellationToken)
    {
        var browserFolder = WebView2RuntimeLocator.TryResolveBundledFolder();
        if (browserFolder is null)
        {
            _logger.Warning("Bundled WebView2 runtime not found on Windows 10");
            App.StartupTrace("WebView2 bundled runtime missing on Windows 10");
            if (_runtimeDiagnosticEventSink is { } sink)
            {
                var evt = new RuntimeDiagnosticEvent(
                    eventId: "",
                    ts: default,
                    sequence: 0,
                    sessionId: null,
                    runId: null,
                    turnId: null,
                    attemptId: null,
                    parentAttemptId: null,
                    toolCallId: null,
                    messageId: null,
                    component: RuntimeDiagnosticComponent.UiWebview,
                    phase: RuntimeDiagnosticPhase.Initialize,
                    eventType: RuntimeDiagnosticErrorCodes.UiWebviewInitFailed,
                    severity: RuntimeDiagnosticSeverity.Error,
                    errorCode: RuntimeDiagnosticErrorCodes.UiWebviewInitFailed,
                    message: "Bundled WebView2 runtime not found on Windows 10");
                await sink.EnqueueAsync(evt, cancellationToken).ConfigureAwait(false);
            }
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
            if (_runtimeDiagnosticEventSink is { } sink)
            {
                var evt = new RuntimeDiagnosticEvent(
                    eventId: "",
                    ts: default,
                    sequence: 0,
                    sessionId: null,
                    runId: null,
                    turnId: null,
                    attemptId: null,
                    parentAttemptId: null,
                    toolCallId: null,
                    messageId: null,
                    component: RuntimeDiagnosticComponent.UiWebview,
                    phase: RuntimeDiagnosticPhase.Initialize,
                    eventType: RuntimeDiagnosticErrorCodes.UiWebviewInitFailed,
                    severity: RuntimeDiagnosticSeverity.Error,
                    errorCode: RuntimeDiagnosticErrorCodes.UiWebviewInitFailed,
                    message: $"Bundled WebView2 runtime failed ({ex.Message})");
                await sink.EnqueueAsync(evt, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }
    }

    private string EnsureUserDataFolder(string modeFolderName)
    {
        var userDataFolder = UserDataFolder(_paths.RootPath, modeFolderName);
        Directory.CreateDirectory(userDataFolder);
        return userDataFolder;
    }
}
