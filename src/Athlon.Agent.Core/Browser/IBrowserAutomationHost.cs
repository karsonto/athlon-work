namespace Athlon.Agent.Core.Browser;

public sealed record BrowserPageInfo(string Url, string Title);

public enum BrowserNavigateAction
{
    Url,
    Back,
    Forward,
    Reload,
}

/// <summary>UI-agnostic host for Browser Tab WebView automation.</summary>
public interface IBrowserAutomationHost
{
    Task EnsureBrowserTabAsync(CancellationToken cancellationToken = default);

    Task NavigateAsync(BrowserNavigateAction action, string? url = null, CancellationToken cancellationToken = default);

    Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an ARIA host operation in the active Browser WebView.
    /// Returns a JSON object string (not double-encoded) with at least { "ok": bool, ... }.
    /// </summary>
    Task<string> ExecuteAriaAsync(string operation, string? argsJson = null, CancellationToken cancellationToken = default);

    Task<BrowserNetworkListResult> ListNetworkEntriesAsync(
        int limit,
        string? urlContains,
        CancellationToken cancellationToken = default);

    Task<BrowserNetworkEntryDetail> GetNetworkEntryAsync(
        string requestId,
        CancellationToken cancellationToken = default);

    Task<BrowserConsoleReadResult> ReadConsoleAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the cookies the browser stores for the given URL (or the active tab's
    /// URL when null). The URL may be an absolute http(s) URL or a bare host name.
    /// </summary>
    Task<IReadOnlyList<BrowserCookieEntry>> GetCookiesAsync(
        string? url,
        CancellationToken cancellationToken = default);
}
