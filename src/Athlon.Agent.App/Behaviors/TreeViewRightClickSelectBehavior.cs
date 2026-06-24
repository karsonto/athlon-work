using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Xaml.Behaviors;

namespace Athlon.Agent.App.Behaviors;

public sealed class TreeViewRightClickSelectBehavior : Behavior<TreeView>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
        base.OnDetaching();
    }

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var treeViewItem = FindAncestor<TreeViewItem>(source);
        if (treeViewItem is null)
        {
            return;
        }

        treeViewItem.IsSelected = true;
        treeViewItem.Focus();
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
