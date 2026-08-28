using System.Collections.Concurrent;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services.Browser;

/// <summary>Maps Browser workspace tab ids to live DevTools capture sessions.</summary>
public sealed class BrowserDevToolsRegistry
{
    private readonly ConcurrentDictionary<Guid, BrowserDevToolsSession> _sessions = new();

    public async Task AttachAsync(Guid tabId, CoreWebView2 webView)
    {
        Detach(tabId);

        var session = new BrowserDevToolsSession(webView);
        await session.EnableAsync().ConfigureAwait(false);
        _sessions[tabId] = session;
    }

    public void Detach(Guid tabId)
    {
        if (_sessions.TryRemove(tabId, out var session))
        {
            session.Dispose();
        }
    }

    public bool TryGet(Guid tabId, out BrowserDevToolsSession? session) =>
        _sessions.TryGetValue(tabId, out session);
}
