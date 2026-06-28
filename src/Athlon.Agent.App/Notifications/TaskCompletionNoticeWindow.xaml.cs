using System.Windows;
using System.Windows.Threading;

namespace Athlon.Agent.App.Notifications;

public partial class TaskCompletionNoticeWindow : Window
{
    private const double ScreenMargin = 16;
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(3);

    public TaskCompletionNoticeWindow(string title, string subtitle)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();

        var timer = new DispatcherTimer { Interval = AutoCloseDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }

    private void PositionBottomRight()
    {
        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - ScreenMargin;
        Top = workArea.Bottom - ActualHeight - ScreenMargin;
    }
}
