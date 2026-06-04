using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Athlon.Agent.App.Services;

internal static class ChatScrollHelper
{
    public static bool HasTextSelection(DependencyObject? root)
    {
        if (root is null)
        {
            return false;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FlowDocumentScrollViewer viewer
                && viewer.Selection is { IsEmpty: false })
            {
                return true;
            }

            if (HasTextSelection(child))
            {
                return true;
            }
        }

        return false;
    }
}
