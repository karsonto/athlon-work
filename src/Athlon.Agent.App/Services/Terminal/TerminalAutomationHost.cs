using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Terminal;
using EasyWindowsTerminalControl;

namespace Athlon.Agent.App.Services.Terminal;

public sealed class TerminalAutomationHost : ITerminalAutomationHost
{
    private readonly WorkspacePaneViewModel _pane;
    private readonly TerminalSessionRegistry _registry;

    public TerminalAutomationHost(
        WorkspacePaneViewModel pane,
        TerminalSessionRegistry registry)
    {
        _pane = pane;
        _registry = registry;
    }

    public Task EnsureTerminalTabAsync(CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = await EnsureTerminalTabCoreAsync(cancellationToken).ConfigureAwait(true);
            EnsureReady(tab);
            _ = await WaitForSessionAsync(tab.Id, cancellationToken).ConfigureAwait(true);
        }, cancellationToken);

    public Task<TerminalSessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = ResolveTargetTab() ?? await EnsureTerminalTabCoreAsync(cancellationToken).ConfigureAwait(true);
            EnsureReady(tab);
            return BuildSessionInfo(tab);
        }, cancellationToken);

    public Task SendInputAsync(string text, bool appendNewline = true, CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(text) && !appendNewline)
            {
                throw new ArgumentException("Provide text or set appendNewline to true.", nameof(text));
            }

            var tab = await EnsureTerminalTabCoreAsync(cancellationToken).ConfigureAwait(true);
            EnsureReady(tab);
            var entry = await WaitForSessionAsync(tab.Id, cancellationToken).ConfigureAwait(true);
            TerminalInputWriter.Write(entry.Session, text, appendNewline, tab.UseWin32Input);
        }, cancellationToken);

    public Task<TerminalOutputSnapshot> ReadOutputAsync(int maxChars = 8000, CancellationToken cancellationToken = default) =>
        UiDispatcherHelper.RunAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = ResolveTargetTab() ?? await EnsureTerminalTabCoreAsync(cancellationToken).ConfigureAwait(true);
            EnsureReady(tab);
            _ = await WaitForSessionAsync(tab.Id, cancellationToken).ConfigureAwait(true);
            return tab.OutputBuffer.Snapshot(maxChars);
        }, cancellationToken);

    private async Task<TerminalWorkspaceTabViewModel> EnsureTerminalTabCoreAsync(CancellationToken cancellationToken)
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

        return await _pane.AddTerminalTabAndActivateAsync(cancellationToken).ConfigureAwait(true);
    }

    private static void EnsureReady(TerminalWorkspaceTabViewModel tab)
    {
        if (!string.IsNullOrWhiteSpace(tab.StartupError))
        {
            throw new InvalidOperationException(tab.StartupError);
        }
    }

    private TerminalWorkspaceTabViewModel? ResolveTargetTab()
    {
        if (_pane.ActiveTab is TerminalWorkspaceTabViewModel active)
        {
            return active;
        }

        return _pane.Tabs.OfType<TerminalWorkspaceTabViewModel>().LastOrDefault();
    }

    private async Task<TerminalSessionEntry> WaitForSessionAsync(Guid tabId, CancellationToken cancellationToken)
    {
        const int maxAttempts = 50;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_registry.TryGet(tabId, out var entry) && entry is not null)
            {
                return entry;
            }

            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }

        throw new InvalidOperationException("Terminal ConPTY session is not ready yet.");
    }

    private TerminalSessionInfo BuildSessionInfo(TerminalWorkspaceTabViewModel tab)
    {
        var attached = _registry.TryGet(tab.Id, out _);
        var alive = tab.Session is not null && !tab.IsDisposed;
        return new TerminalSessionInfo(
            tab.Title ?? string.Empty,
            tab.WorkingDirectory,
            IsAttached: attached,
            ProcessAlive: alive);
    }
}
