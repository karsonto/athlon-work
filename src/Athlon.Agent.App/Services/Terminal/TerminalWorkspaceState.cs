using System.Collections.Specialized;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Terminal;

namespace Athlon.Agent.App.Services.Terminal;

public sealed class TerminalWorkspaceState : ITerminalWorkspaceState
{
    private readonly WorkspacePaneViewModel _pane;
    private volatile bool _hasOpenTerminalTab;

    public TerminalWorkspaceState(WorkspacePaneViewModel pane)
    {
        _pane = pane;
        _pane.Tabs.CollectionChanged += OnTabsChanged;
        Refresh();
    }

    public bool HasOpenTerminalTab => _hasOpenTerminalTab;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh() =>
        _hasOpenTerminalTab = _pane.Tabs.OfType<TerminalWorkspaceTabViewModel>().Any();
}
