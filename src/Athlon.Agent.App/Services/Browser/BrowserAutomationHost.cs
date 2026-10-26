using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services.Browser;

public sealed class BrowserAutomationHost : IBrowserAutomationHost
{
    internal const string AriaHostEmbeddedResourceName =
        "Athlon.Agent.App.Assets.Browser.athlon-aria-host.js";

    private readonly WorkspacePaneViewModel _pane;
    private readonly BrowserWebViewRegistry _registry;
    private readonly BrowserDevToolsRegistry _devToolsRegistry;
    private readonly IAgentRunContextAccessor _runContext;
    private readonly IImageAttachmentStore _images;
    private readonly IImageAttachmentPruner _pruner;

    public BrowserAutomationHost(
        WorkspacePaneViewModel pane,
        BrowserWebViewRegistry registry,
        BrowserDevToolsRegistry devToolsRegistry,
        IAgentRunContextAccessor runContext,
        IImageAttachmentStore images,
        IImageAttachmentPruner pruner)
    {
        _pane = pane;
        _registry = registry;
        _devToolsRegistry = devToolsRegistry;
        _runContext = runContext;
        _images = images;
        _pruner = pruner;
    }

    public Task EnsureBrowserTabAsync(CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = EnsureBrowserTabCore();
            return Task.CompletedTask;
        }, cancellationToken);

    public Task NavigateAsync(
        BrowserNavigateAction action,
        string? url = null,
        CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = EnsureBrowserTabCore();
            var webView = await WaitForWebViewAsync(tab.Id, cancellationToken).ConfigureAwait(true);
            switch (action)
            {
                case BrowserNavigateAction.Back:
                    if (webView.CanGoBack)
                    {
                        webView.GoBack();
                    }
                    break;
                case BrowserNavigateAction.Forward:
                    if (webView.CanGoForward)
                    {
                        webView.GoForward();
                    }
                    break;
                case BrowserNavigateAction.Reload:
                    webView.Reload();
                    break;
                default:
                {
                    var normalized = BrowserWorkspaceTabViewModel.NormalizeUrl(url);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        throw new InvalidOperationException("A valid http(s) or file:// URL is required.");
                    }

                    tab.AddressText = normalized;
                    tab.CurrentUrl = normalized;
                    webView.Navigate(normalized);
                    break;
                }
            }
        }, cancellationToken);

    public Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = EnsureBrowserTabCore();
            var webView = await WaitForWebViewAsync(tab.Id, cancellationToken).ConfigureAwait(true);
            return new BrowserPageInfo(
                webView.Source ?? tab.CurrentUrl ?? string.Empty,
                webView.DocumentTitle ?? tab.Title ?? string.Empty);
        }, cancellationToken);

    public async Task<BrowserScreenshotCapture?> CaptureScreenshotAsync(
        bool fullPage = false,
        CancellationToken cancellationToken = default)
    {
        var sessionId = _runContext.Current?.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var captured = await UiDispatcherHelper.RunAsync(
            () => ReadPngAsync(fullPage, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (captured is null)
        {
            return null;
        }

        var attachment = _images.SaveBrowserFrame(sessionId, captured.Value.MimeType, captured.Value.Bytes);
        await _pruner.PruneAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new BrowserScreenshotCapture(
            attachment,
            captured.Value.Url,
            captured.Value.Title,
            captured.Value.FullPage,
            captured.Value.Clipped);
    }

    public Task<string> ExecuteAriaAsync(
        string operation,
        string? argsJson = null,
        CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(operation))
            {
                throw new ArgumentException("ARIA operation is required.", nameof(operation));
            }

            // Activate the Browser tab so ContentControl mounts WebView2 and registers it.
            var tab = EnsureBrowserTabCore();
            var webView = await WaitForWebViewAsync(tab.Id, cancellationToken).ConfigureAwait(true);
            await EnsureAriaHostInjectedAsync(webView).ConfigureAwait(true);

            var argsLiteral = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson.Trim();
            using (JsonDocument.Parse(argsLiteral))
            {
            }

            var opLiteral = JsonSerializer.Serialize(operation);
            var script =
                "(async function(){" +
                "const host=window.__athlonAria;" +
                "if(!host||typeof host.invoke!=='function'){" +
                "return JSON.stringify({ok:false,error:'ARIA host script is not loaded'});}" +
                "try{" +
                $"const result=await host.invoke({opLiteral},{argsLiteral});" +
                "return JSON.stringify(result);" +
                "}catch(e){return JSON.stringify({ok:false,error:String(e&&e.message||e)});} " +
                "})()";

            // ExecuteScriptAsync does not await Promises (returns "{}"); use CDP instead.
            var raw = await EvaluateScriptAwaitingPromiseAsync(webView, script).ConfigureAwait(true);
            return UnwrapExecuteScriptJson(raw);
        }, cancellationToken);

    public Task<BrowserNetworkListResult> ListNetworkEntriesAsync(
        int limit,
        string? urlContains,
        CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await ResolveDevToolsSessionAsync(cancellationToken).ConfigureAwait(true);
            return session.ListNetworkEntries(limit, urlContains);
        }, cancellationToken);

    public Task<BrowserNetworkEntryDetail> GetNetworkEntryAsync(
        string requestId,
        CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new ArgumentException("requestId is required.", nameof(requestId));
            }

            var session = await ResolveDevToolsSessionAsync(cancellationToken).ConfigureAwait(true);
            return await session.GetNetworkEntryAsync(requestId.Trim(), cancellationToken).ConfigureAwait(true);
        }, cancellationToken);

    public Task<BrowserConsoleReadResult> ReadConsoleAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await ResolveDevToolsSessionAsync(cancellationToken).ConfigureAwait(true);
            return session.ReadConsoleEntries(limit);
        }, cancellationToken);

    public Task<IReadOnlyList<BrowserCookieEntry>> GetCookiesAsync(
        string? url,
        CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = EnsureBrowserTabCore();
            var webView = await WaitForWebViewAsync(tab.Id, cancellationToken).ConfigureAwait(true);
            var target = ResolveCookieUrl(url, tab, webView);
            var cookies = await webView.CookieManager.GetCookiesAsync(target).ConfigureAwait(true);
            var entries = new List<BrowserCookieEntry>(cookies.Count);
            foreach (var cookie in cookies)
            {
                // .NET wrapper: Expires is a DateTime; DateTime.MinValue marks a session cookie.
                var isSession = cookie.Expires == DateTime.MinValue;
                entries.Add(new BrowserCookieEntry(
                    cookie.Name ?? string.Empty,
                    cookie.Value ?? string.Empty,
                    cookie.Domain,
                    cookie.Path,
                    isSession,
                    isSession
                        ? null
                        : DateTime.SpecifyKind(cookie.Expires, DateTimeKind.Utc)
                            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    cookie.IsHttpOnly,
                    cookie.IsSecure,
                    cookie.SameSite.ToString()));
            }

            return (IReadOnlyList<BrowserCookieEntry>)entries;
        }, cancellationToken);

    private static string ResolveCookieUrl(string? url, BrowserWorkspaceTabViewModel tab, CoreWebView2 webView)
    {
        var candidate = url;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            // CoreWebView2.Source is a plain string (unlike a Uri).
            candidate = string.IsNullOrEmpty(webView.Source) ? tab.CurrentUrl : webView.Source;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException(
                "No URL available to read cookies for. Pass url or navigate to a page first.");
        }

        candidate = candidate.Trim();
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"A valid http(s) URL is required to read cookies (got: {url}).");
        }

        return uri.AbsoluteUri;
    }

    private BrowserWorkspaceTabViewModel EnsureBrowserTabCore()
    {
        var existing = ResolveTargetTab();
        if (existing is not null)
        {
            if (!ReferenceEquals(_pane.ActiveTab, existing))
            {
                _pane.ActiveTab = existing;
            }

            return existing;
        }

        return _pane.AddBrowserTabAndActivate();
    }

    private const int MaxFullPageEdgeCssPx = 16_384;

    private readonly record struct VisiblePng(
        byte[] Bytes,
        string MimeType,
        string Url,
        string Title,
        bool FullPage,
        bool Clipped);

    private async Task<VisiblePng?> ReadPngAsync(bool fullPage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tab = ResolveTargetTab();
        if (tab is null)
        {
            return null;
        }

        var activated = !ReferenceEquals(_pane.ActiveTab, tab);
        if (activated)
        {
            _pane.ActiveTab = tab;
        }

        var webView = await WaitForWebViewAsync(tab.Id, cancellationToken).ConfigureAwait(true);
        await WaitForScreenshotReadyAsync(tab, webView, activated, cancellationToken).ConfigureAwait(true);
        var captured = fullPage
            ? await CaptureFullPagePngAsync(webView).ConfigureAwait(true)
            : await CaptureVisiblePngAsync(webView).ConfigureAwait(true);
        if (captured.Bytes is not { Length: > 0 })
        {
            return null;
        }

        var shrunk = BrowserScreenshotEncoder.Shrink(captured.Bytes);
        return new VisiblePng(
            shrunk.Bytes,
            shrunk.MimeType,
            webView.Source ?? tab.CurrentUrl ?? string.Empty,
            webView.DocumentTitle ?? tab.Title ?? string.Empty,
            fullPage,
            captured.Clipped);
    }

    /// <summary>
    /// Avoids stale tool-card PNGs: wait for navigation to settle, WPF layout after tab
    /// activation, and at least two animation frames before capturing.
    /// </summary>
    private static async Task WaitForScreenshotReadyAsync(
        BrowserWorkspaceTabViewModel tab,
        CoreWebView2 webView,
        bool tabJustActivated,
        CancellationToken cancellationToken)
    {
        // After ContentControl remounts the Browser tab, give layout/render a chance
        // before CapturePreview / CDP would otherwise snapshot an old surface.
        var layoutPasses = tabJustActivated ? 5 : 2;
        for (var i = 0; i < layoutPasses; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (tab.IsLoading && DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
        }

        try
        {
            await EvaluateScriptAwaitingPromiseAsync(
                    webView,
                    """
                    (async function () {
                      if (document.readyState === 'loading') {
                        await new Promise(function (resolve) {
                          document.addEventListener('DOMContentLoaded', resolve, { once: true });
                        });
                      }
                      await new Promise(function (resolve) {
                        requestAnimationFrame(function () {
                          requestAnimationFrame(resolve);
                        });
                      });
                      return true;
                    })()
                    """)
                .ConfigureAwait(true);
        }
        catch
        {
            // Best-effort; still attempt the capture.
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Viewport capture via CDP (not CapturePreviewAsync). Preview bitmaps can lag behind
    /// SPA / navigation updates and show up as stale images on the tool card.
    /// </summary>
    private static async Task<(byte[]? Bytes, bool Clipped)> CaptureVisiblePngAsync(CoreWebView2 webView)
    {
        await webView.CallDevToolsProtocolMethodAsync("Page.enable", "{}").ConfigureAwait(true);
        var request = JsonSerializer.Serialize(new
        {
            format = "png",
            fromSurface = true,
            captureBeyondViewport = false
        });
        return await CaptureCdpPngAsync(webView, request, clipped: false).ConfigureAwait(true);
    }

    private static async Task<(byte[]? Bytes, bool Clipped)> CaptureFullPagePngAsync(CoreWebView2 webView)
    {
        await webView.CallDevToolsProtocolMethodAsync("Page.enable", "{}").ConfigureAwait(true);
        var metricsJson = await webView.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}")
            .ConfigureAwait(true);
        using var metrics = JsonDocument.Parse(metricsJson);
        if (!TryGetContentSize(metrics.RootElement, out var width, out var height))
        {
            throw new InvalidOperationException("Page layout metrics did not include a content size.");
        }

        var clipped = width > MaxFullPageEdgeCssPx || height > MaxFullPageEdgeCssPx;
        width = Math.Min(width, MaxFullPageEdgeCssPx);
        height = Math.Min(height, MaxFullPageEdgeCssPx);
        var scale = BrowserScreenshotEncoder.FullPageScale(width, height);
        var request = JsonSerializer.Serialize(new
        {
            format = "png",
            fromSurface = true,
            captureBeyondViewport = true,
            clip = new { x = 0, y = 0, width, height, scale }
        });
        return await CaptureCdpPngAsync(webView, request, clipped).ConfigureAwait(true);
    }

    private static async Task<(byte[]? Bytes, bool Clipped)> CaptureCdpPngAsync(
        CoreWebView2 webView,
        string requestJson,
        bool clipped)
    {
        var shotJson = await webView.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", requestJson)
            .ConfigureAwait(true);
        using var shot = JsonDocument.Parse(shotJson);
        var data = shot.RootElement.TryGetProperty("data", out var dataElement)
            ? dataElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(data))
        {
            return (null, clipped);
        }

        return (Convert.FromBase64String(data), clipped);
    }

    private static bool TryGetContentSize(JsonElement metrics, out double width, out double height)
    {
        width = 0;
        height = 0;
        if (!metrics.TryGetProperty("cssContentSize", out var content)
            && !metrics.TryGetProperty("contentSize", out content))
        {
            return false;
        }

        if (!content.TryGetProperty("width", out var widthElement)
            || !content.TryGetProperty("height", out var heightElement)
            || widthElement.ValueKind != JsonValueKind.Number
            || heightElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        width = widthElement.GetDouble();
        height = heightElement.GetDouble();
        return width >= 1 && height >= 1;
    }

    private BrowserWorkspaceTabViewModel? ResolveTargetTab()
    {
        if (_pane.ActiveTab is BrowserWorkspaceTabViewModel active)
        {
            return active;
        }

        return _pane.Tabs.OfType<BrowserWorkspaceTabViewModel>().LastOrDefault();
    }

    private async Task<CoreWebView2> WaitForWebViewAsync(Guid tabId, CancellationToken cancellationToken)
    {
        const int maxAttempts = 50;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_registry.TryGet(tabId, out var webView) && webView is not null)
            {
                return webView;
            }

            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }

        throw new InvalidOperationException("Browser WebView2 is not ready yet.");
    }

    private async Task<BrowserDevToolsSession> ResolveDevToolsSessionAsync(CancellationToken cancellationToken)
    {
        var tab = EnsureBrowserTabCore();
        await WaitForWebViewAsync(tab.Id, cancellationToken).ConfigureAwait(true);

        if (_devToolsRegistry.TryGet(tab.Id, out var session) && session is not null)
        {
            return session;
        }

        throw new InvalidOperationException("Browser DevTools session is not ready yet.");
    }

    private static async Task EnsureAriaHostInjectedAsync(CoreWebView2 webView)
    {
        if (await IsAriaHostPresentAsync(webView).ConfigureAwait(true))
        {
            return;
        }

        var script = TryLoadAriaHostScript();
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new InvalidOperationException(
                "ARIA host script is missing from the app package (Assets/Browser/athlon-aria-host.js).");
        }

        try
        {
            await webView.AddScriptToExecuteOnDocumentCreatedAsync(script).ConfigureAwait(true);
        }
        catch
        {
            // May already be registered for this CoreWebView2; continue with immediate inject.
        }

        await webView.ExecuteScriptAsync(script).ConfigureAwait(true);

        if (!await IsAriaHostPresentAsync(webView).ConfigureAwait(true))
        {
            throw new InvalidOperationException(
                "ARIA host script injection failed for the current page.");
        }
    }

    private static async Task<bool> IsAriaHostPresentAsync(CoreWebView2 webView)
    {
        try
        {
            var raw = await webView.ExecuteScriptAsync(
                    "(function(){return !!(window.__athlonAria && window.__athlonAria.__version==='2' && typeof window.__athlonAria.invoke==='function');})()")
                .ConfigureAwait(true);
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string UnwrapExecuteScriptJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return """{"ok":false,"error":"Empty script result"}""";
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                return doc.RootElement.GetString() ?? """{"ok":false,"error":"Empty script result"}""";
            }

            return raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>
    /// WebView2 ExecuteScriptAsync does not await Promises (serializes them as "{}").
    /// Use CDP Runtime.evaluate with awaitPromise=true instead.
    /// </summary>
    private static async Task<string> EvaluateScriptAwaitingPromiseAsync(CoreWebView2 webView, string expression)
    {
        var requestJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["awaitPromise"] = true,
            ["returnByValue"] = true
        });

        var responseJson = await webView
            .CallDevToolsProtocolMethodAsync("Runtime.evaluate", requestJson)
            .ConfigureAwait(true);

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            var text = exceptionDetails.TryGetProperty("text", out var textEl)
                ? textEl.GetString()
                : null;
            var description = exceptionDetails.TryGetProperty("exception", out var exEl)
                && exEl.TryGetProperty("description", out var descEl)
                    ? descEl.GetString()
                    : null;
            var message = !string.IsNullOrWhiteSpace(description)
                ? description
                : (text ?? "JavaScript evaluation failed");
            return JsonElementFormatter.SerializeForDisplay(new { ok = false, error = message });
        }

        if (!root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("value", out var value))
        {
            return """{"ok":false,"error":"Empty CDP evaluate result"}""";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? """{"ok":false,"error":"Empty script result"}""",
            JsonValueKind.Object or JsonValueKind.Array => JsonElementFormatter.FormatForDisplay(value, indented: true),
            JsonValueKind.Null or JsonValueKind.Undefined =>
                """{"ok":false,"error":"Empty script result"}""",
            _ => JsonElementFormatter.FormatForDisplay(value, indented: true)
        };
    }

    internal static string? TryLoadAriaHostScript()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Browser", "athlon-aria-host.js");
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }
        catch
        {
            // Fall through to embedded resource.
        }

        try
        {
            var assembly = typeof(BrowserAutomationHost).Assembly;
            using var stream = assembly.GetManifestResourceStream(AriaHostEmbeddedResourceName);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
