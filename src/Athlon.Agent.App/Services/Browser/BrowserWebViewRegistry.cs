using System.Collections.Concurrent;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services.Browser;

/// <summary>Maps Browser workspace tab ids to their live CoreWebView2 instances.</summary>
public sealed class BrowserWebViewRegistry
{
    private readonly ConcurrentDictionary<Guid, CoreWebView2> _webViews = new();

    public void Register(Guid tabId, CoreWebView2 webView) =>
        _webViews[tabId] = webView;

    public void Unregister(Guid tabId) =>
        _webViews.TryRemove(tabId, out _);

    public bool TryGet(Guid tabId, out CoreWebView2? webView) =>
        _webViews.TryGetValue(tabId, out webView);

    public IReadOnlyCollection<Guid> RegisteredTabIds => _webViews.Keys.ToArray();
}
