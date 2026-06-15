using System.Windows;

namespace Athlon.Agent.App.Licensing;

public partial class ImpSsoLoginWaitingWindow : Window
{
    private const double ScreenMargin = 16;

    public ImpSsoLoginWaitingWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - ScreenMargin;
        Top = workArea.Bottom - ActualHeight - ScreenMargin;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
