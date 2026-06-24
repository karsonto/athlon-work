using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Controls;

public partial class KnowledgeDocumentsDropZone : UserControl
{
    public KnowledgeDocumentsDropZone()
    {
        InitializeComponent();
    }

    private KnowledgeViewModel? ViewModel => DataContext as KnowledgeViewModel;

    private void DocumentTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is KnowledgeTreeNodeViewModel node)
        {
            ViewModel?.SelectTreeNode(node);
        }
    }

    private void DocumentTree_OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DragOverlay.Opacity = 1;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void DocumentTree_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DocumentTree_OnDragLeave(object sender, DragEventArgs e)
    {
        DragOverlay.Opacity = 0;
        e.Handled = true;
    }

    private async void DocumentTree_OnDrop(object sender, DragEventArgs e)
    {
        DragOverlay.Opacity = 0;
        if (ViewModel is not null && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            await ViewModel.ImportDocumentsAsync(files);
        }

        e.Handled = true;
    }
}
