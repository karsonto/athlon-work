namespace Athlon.Agent.Core.Browser;

public sealed class NullBrowserWorkspaceState : IBrowserWorkspaceState
{
    public static NullBrowserWorkspaceState Instance { get; } = new();

    public bool HasOpenBrowserTab => false;
}

public sealed class NullBrowserAutomationHost : IBrowserAutomationHost
{
    public static NullBrowserAutomationHost Instance { get; } = new();

    public Task EnsureBrowserTabAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Browser automation host is not available."));

    public Task NavigateAsync(BrowserNavigateAction action, string? url = null, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Browser automation host is not available."));

    public Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<BrowserPageInfo>(new InvalidOperationException("Browser automation host is not available."));

    public Task<string> ExecuteAriaAsync(string operation, string? argsJson = null, CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new InvalidOperationException("Browser automation host is not available."));
}
