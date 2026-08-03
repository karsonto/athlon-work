using System.Collections.Specialized;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.App.Services.Browser;

public sealed class BrowserWorkspaceState : IBrowserWorkspaceState
{
    private readonly WorkspacePaneViewModel _pane;
    private volatile bool _hasOpenBrowserTab;

    public BrowserWorkspaceState(WorkspacePaneViewModel pane)
    {
        _pane = pane;
        _pane.Tabs.CollectionChanged += OnTabsChanged;
        Refresh();
    }

    public bool HasOpenBrowserTab => _hasOpenBrowserTab;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh() =>
        _hasOpenBrowserTab = _pane.Tabs.OfType<BrowserWorkspaceTabViewModel>().Any();
}
