using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Views;

public partial class ContextSidebarView : UserControl
{
    public ContextSidebarView()
    {
        InitializeComponent();
    }

    private async void WorkspaceTreeItem_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: WorkspaceTreeNodeViewModel node })
        {
            return;
        }

        if (Window.GetWindow(this)?.DataContext is MainShellViewModel viewModel)
        {
            await viewModel.Sidebar.ExpandWorkspaceTreeNodeAsync(node).ConfigureAwait(true);
        }
    }
}
