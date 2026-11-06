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
        if (e.OriginalSource is DependencyObject source &&
            FindAncestorButton(source) is not null)
        {
            return;
        }

        if (FindTeamsVm() is { } vm &&
            sender is FrameworkElement { DataContext: TeamMemberItemViewModel member })
        {
            vm.SelectMemberCommand.Execute(member);
        }
    }

    private void OnDeleteWorkerButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Button handles the action; card selection listens on MouseLeftButtonUp.
    }

    private static Button? FindAncestorButton(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Button button)
            {
                return button;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
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
