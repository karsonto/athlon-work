using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Views;

public partial class KnowledgePageView : UserControl
{
    public KnowledgePageView()
    {
        InitializeComponent();
    }

    private void GraphEntityTypesBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: KnowledgeViewModel viewModel })
        {
            viewModel.CommitEntityTypes();
        }
    }
}
