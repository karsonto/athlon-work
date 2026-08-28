using System.Collections.Concurrent;
using System.Text.Json;
using Athlon.Agent.Core.Browser;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Services.Browser;

public sealed class BrowserDevToolsSession : IDisposable
{
    private readonly CoreWebView2 _webView;
    private readonly BrowserDevToolsCaptureBuffer _buffer = new();
    private readonly List<(string EventName, EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> Handler)> _subscriptions = [];
    private bool _enabled;
    private bool _disposed;

    public BrowserDevToolsSession(CoreWebView2 webView)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
    }

    public async Task EnableAsync()
    {
        if (_enabled)
        {
            return;
        }

        await _webView.CallDevToolsProtocolMethodAsync("Network.enable", "{}").ConfigureAwait(true);
        await _webView.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}").ConfigureAwait(true);

        Subscribe("Network.requestWillBeSent", json => _buffer.IngestNetworkEvent("Network.requestWillBeSent", json));
        Subscribe("Network.responseReceived", json => _buffer.IngestNetworkEvent("Network.responseReceived", json));
        Subscribe("Network.loadingFinished", json => _buffer.IngestNetworkEvent("Network.loadingFinished", json));
        Subscribe("Network.loadingFailed", json => _buffer.IngestNetworkEvent("Network.loadingFailed", json));
        Subscribe("Runtime.consoleAPICalled", json => _buffer.IngestConsoleEvent("Runtime.consoleAPICalled", json));
        Subscribe("Runtime.exceptionThrown", json => _buffer.IngestConsoleEvent("Runtime.exceptionThrown", json));

        _enabled = true;
    }

    public BrowserNetworkListResult ListNetworkEntries(int limit, string? urlContains) =>
        _buffer.ListNetworkEntries(limit, urlContains);

    public async Task<BrowserNetworkEntryDetail> GetNetworkEntryAsync(string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_buffer.TryGetNetworkState(requestId, out var state) || state is null)
        {
            throw new InvalidOperationException($"Network request '{requestId}' was not found in the capture buffer.");
        }

        string? responseBody = null;
        var responseBodyIsBase64 = false;
        string? responseBodyError = null;

        if (state.HasResponseBody || state.Status is not null)
        {
            try
            {
                var parameters = JsonSerializer.Serialize(new { requestId });
                var responseJson = await _webView
                    .CallDevToolsProtocolMethodAsync("Network.getResponseBody", parameters)
                    .ConfigureAwait(true);

                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String)
                {
                    responseBody = bodyEl.GetString();
                }

                responseBodyIsBase64 = root.TryGetProperty("base64Encoded", out var b64El)
                    && b64El.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex)
            {
                responseBodyError = ex.Message;
            }
        }

        return new BrowserNetworkEntryDetail(
            state.ToSummary(),
            state.RequestHeaders,
            state.RequestBody,
            state.ResponseHeaders,
            responseBody,
            responseBodyIsBase64,
            responseBodyError);
    }

    public BrowserConsoleReadResult ReadConsoleEntries(int limit) =>
        _buffer.ReadConsoleEntries(limit);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (eventName, handler) in _subscriptions)
        {
            try
            {
                _webView.GetDevToolsProtocolEventReceiver(eventName).DevToolsProtocolEventReceived -= handler;
            }
            catch
            {
                // Best-effort unsubscribe.
            }
        }

        _subscriptions.Clear();

        if (_enabled)
        {
            try
            {
                _ = _webView.CallDevToolsProtocolMethodAsync("Network.disable", "{}");
            }
            catch
            {
                // ignore
            }

            try
            {
                _ = _webView.CallDevToolsProtocolMethodAsync("Runtime.disable", "{}");
            }
            catch
            {
                // ignore
            }
        }
    }

    private void Subscribe(string eventName, Action<string> ingest)
    {
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler = (_, args) =>
        {
            try
            {
                ingest(args.ParameterObjectAsJson);
            }
            catch
            {
                // Ignore malformed CDP payloads.
            }
        };

        _webView.GetDevToolsProtocolEventReceiver(eventName).DevToolsProtocolEventReceived += handler;
        _subscriptions.Add((eventName, handler));
    }
}
