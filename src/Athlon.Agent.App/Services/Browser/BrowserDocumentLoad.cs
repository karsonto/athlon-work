using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services.Browser;

/// <summary>
/// Loads documents into a Browser WebView.
/// file:// responses are reused from the profile cache on a plain <c>Navigate</c>, and
/// navigating to the URL already in <c>Source</c> does not create a new document.
/// Local files are loaded with the cache bypassed, and same-file opens hard-reload.
/// </summary>
internal static class BrowserDocumentLoad
{
    internal const int NavigationTimeoutSeconds = 15;

    internal enum FileLoad
    {
        DefaultNavigate,
        FileNavigateBypassingCache,
        FileReloadIgnoringCache,
    }

    internal static bool IsFileUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile;

    internal static bool IsSameFile(string? currentUrl, string targetUrl)
    {
        if (!IsFileUrl(currentUrl) || !IsFileUrl(targetUrl))
        {
            return false;
        }

        var current = new Uri(currentUrl!);
        var target = new Uri(targetUrl);
        return string.Equals(NormalizePath(current), NormalizePath(target), StringComparison.OrdinalIgnoreCase);
    }

    internal static FileLoad Choose(string? currentUrl, string targetUrl)
    {
        if (!IsFileUrl(targetUrl))
        {
            return FileLoad.DefaultNavigate;
        }

        return IsSameFile(currentUrl, targetUrl)
            ? FileLoad.FileReloadIgnoringCache
            : FileLoad.FileNavigateBypassingCache;
    }

    internal static async Task NavigateAsync(
        CoreWebView2 webView,
        string url,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (Choose(webView.Source, url))
        {
            case FileLoad.FileReloadIgnoringCache:
                await LoadFileBypassingCacheAsync(webView, ReloadIgnoringCacheAsync, cancellationToken)
                    .ConfigureAwait(true);
                return;
            case FileLoad.FileNavigateBypassingCache:
                await LoadFileBypassingCacheAsync(
                        webView,
                        (view, token) => NavigateAndWaitAsync(view, url, token),
                        cancellationToken)
                    .ConfigureAwait(true);
                return;
            default:
                webView.Navigate(url);
                return;
        }
    }

    internal static Task ReloadAsync(CoreWebView2 webView, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFileUrl(webView.Source))
        {
            webView.Reload();
            return Task.CompletedTask;
        }

        return LoadFileBypassingCacheAsync(webView, ReloadIgnoringCacheAsync, cancellationToken);
    }

    private static async Task LoadFileBypassingCacheAsync(
        CoreWebView2 webView,
        Func<CoreWebView2, CancellationToken, Task> load,
        CancellationToken cancellationToken)
    {
        await SetCacheDisabledAsync(webView, disabled: true).ConfigureAwait(true);
        try
        {
            await load(webView, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await SetCacheDisabledAsync(webView, disabled: false).ConfigureAwait(true);
        }
    }

    private static Task NavigateAndWaitAsync(
        CoreWebView2 webView,
        string url,
        CancellationToken cancellationToken) =>
        RunAfterNavigationAsync(webView, () =>
        {
            webView.Navigate(url);
            return Task.CompletedTask;
        }, cancellationToken);

    private static Task ReloadIgnoringCacheAsync(CoreWebView2 webView, CancellationToken cancellationToken) =>
        RunAfterNavigationAsync(webView, async () =>
        {
            try
            {
                var payload = JsonSerializer.Serialize(new Dictionary<string, bool>
                {
                    ["ignoreCache"] = true,
                });
                await webView.CallDevToolsProtocolMethodAsync("Page.reload", payload).ConfigureAwait(true);
            }
            catch
            {
                webView.Reload();
            }
        }, cancellationToken);

    private static async Task SetCacheDisabledAsync(CoreWebView2 webView, bool disabled)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, bool>
            {
                ["cacheDisabled"] = disabled,
            });
            await webView.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", payload)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace(
                disabled
                    ? $"Browser cache bypass failed: {ex.Message}"
                    : $"Browser cache re-enable failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Subscribes to <see cref="CoreWebView2.NavigationCompleted"/> before <paramref name="start"/>
    /// so a file that commits during the start call is not missed.
    /// </summary>
    private static async Task RunAfterNavigationAsync(
        CoreWebView2 webView,
        Func<Task> start,
        CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            webView.NavigationCompleted -= OnCompleted;
            if (e.IsSuccess)
            {
                completed.TrySetResult(true);
                return;
            }

            completed.TrySetException(
                new InvalidOperationException($"Navigation failed ({e.WebErrorStatus})."));
        }

        webView.NavigationCompleted += OnCompleted;
        try
        {
            await start().ConfigureAwait(true);
            await completed.Task
                .WaitAsync(TimeSpan.FromSeconds(NavigationTimeoutSeconds), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Timed out waiting for the local file page to finish loading.");
        }
        finally
        {
            webView.NavigationCompleted -= OnCompleted;
        }
    }

    private static string NormalizePath(Uri uri)
    {
        var path = uri.LocalPath;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
