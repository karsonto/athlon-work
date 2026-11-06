using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Views;

public partial class TeamsPageView : UserControl
{
    public TeamsPageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainShellViewModel shell)
        {
            await shell.TeamsPageVm.RefreshAsync().ConfigureAwait(true);
        }
    }
}
