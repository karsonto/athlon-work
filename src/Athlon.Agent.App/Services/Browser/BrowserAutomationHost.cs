using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Browser;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services.Browser;

public sealed class BrowserAutomationHost : IBrowserAutomationHost
{
    internal const string AriaHostEmbeddedResourceName =
        "Athlon.Agent.App.Assets.Browser.athlon-aria-host.js";

    private readonly WorkspacePaneViewModel _pane;
    private readonly BrowserWebViewRegistry _registry;

    public BrowserAutomationHost(
        WorkspacePaneViewModel pane,
        BrowserWebViewRegistry registry)
    {
        _pane = pane;
        _registry = registry;
    }

    public Task EnsureBrowserTabAsync(CancellationToken cancellationToken = default) =>
        InvokeOnUiAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = EnsureBrowserTabCore();
            return Task.CompletedTask;
        }, cancellationToken);

    public Task NavigateAsync(
        BrowserNavigateAction action,
        string? url = null,
        CancellationToken cancellationToken = default) =>
        InvokeOnUiAsync(async () =>
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
                        throw new InvalidOperationException("A valid http(s) URL is required.");
                    }

                    tab.AddressText = normalized;
                    tab.CurrentUrl = normalized;
                    webView.Navigate(normalized);
                    break;
                }
            }
        }, cancellationToken);

    public Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken cancellationToken = default) =>
        InvokeOnUiAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = EnsureBrowserTabCore();
            var webView = await WaitForWebViewAsync(tab.Id, cancellationToken).ConfigureAwait(true);
            return new BrowserPageInfo(
                webView.Source ?? tab.CurrentUrl ?? string.Empty,
                webView.DocumentTitle ?? tab.Title ?? string.Empty);
        }, cancellationToken);

    public Task<string> ExecuteAriaAsync(
        string operation,
        string? argsJson = null,
        CancellationToken cancellationToken = default) =>
        InvokeOnUiAsync(async () =>
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

    private static async Task InvokeOnUiAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF dispatcher is not available.");

        if (dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        var op = dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
        await op.Task.ConfigureAwait(false);
        await op.Result.ConfigureAwait(false);
    }

    private static async Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF dispatcher is not available.");

        if (dispatcher.CheckAccess())
        {
            return await action().ConfigureAwait(true);
        }

        var op = dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
        await op.Task.ConfigureAwait(false);
        return await op.Result.ConfigureAwait(false);
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
