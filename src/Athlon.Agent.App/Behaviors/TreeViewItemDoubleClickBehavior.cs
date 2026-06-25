using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Athlon.Agent.App.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace Athlon.Agent.App.Behaviors;

public sealed class TreeViewItemDoubleClickBehavior : Behavior<TreeView>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.MouseDoubleClick += OnMouseDoubleClick;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.MouseDoubleClick -= OnMouseDoubleClick;
        base.OnDetaching();
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var treeViewItem = FindAncestor<TreeViewItem>(source);
        if (treeViewItem?.DataContext is not WorkspaceTreeNodeViewModel node)
        {
            return;
        }

        if (node.IsPlaceholder || node.IsExpanderPlaceholder || node.IsDirectory || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        var viewModel = Window.GetWindow(AssociatedObject)?.DataContext as MainShellViewModel;
        if (viewModel?.OpenWorkspaceTreeNodeInEditorCommand.CanExecute(node) == true)
        {
            viewModel.OpenWorkspaceTreeNodeInEditorCommand.Execute(node);
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T matched)
            {
                return matched;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
