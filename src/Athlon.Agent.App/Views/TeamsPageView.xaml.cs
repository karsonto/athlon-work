using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        if (FindTeamsVm() is { } vm)
        {
            await vm.RefreshAsync().ConfigureAwait(true);
        }
    }

    private void OnMemberCardClick(object sender, MouseButtonEventArgs e)
    {
        if (FindTeamsVm() is { } vm &&
            sender is FrameworkElement { DataContext: TeamMemberItemViewModel member })
        {
            vm.SelectMemberCommand.Execute(member);
        }
    }

    private TeamsPageViewModel? FindTeamsVm()
    {
        if (DataContext is MainShellViewModel shell)
        {
            return shell.TeamsPageVm;
        }

        return DataContext as TeamsPageViewModel;
    }
}
