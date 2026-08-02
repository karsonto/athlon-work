using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Controls;

public partial class WorkspaceFilesPanel : UserControl
{
    public WorkspaceFilesPanel()
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

    private void WorkspaceTree_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        var shell = Window.GetWindow(this)?.DataContext as MainShellViewModel;
        if (shell?.CanAcceptRemoteFileDrop == true && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void WorkspaceTree_OnDrop(object sender, DragEventArgs e)
    {
        var shell = Window.GetWindow(this)?.DataContext as MainShellViewModel;
        if (shell is null || !shell.CanAcceptRemoteFileDrop)
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        var hit = FindTreeNode(e.OriginalSource as DependencyObject);
        await shell.UploadLocalPathsToRemoteAsync(
            paths,
            hit?.FullPath,
            hit?.IsDirectory ?? false).ConfigureAwait(true);
        e.Handled = true;
    }

    private static WorkspaceTreeNodeViewModel? FindTreeNode(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TreeViewItem { DataContext: WorkspaceTreeNodeViewModel node })
            {
                return node;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
