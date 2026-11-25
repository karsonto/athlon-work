using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Athlon.Agent.App.Controls;

public partial class WelcomeBuddyIcon : UserControl
{
    private static readonly string[] FaceNames = ["微笑", "眨眼", "开心", "好奇", "得意", "困倦", "惊讶"];

    private Canvas[]? _faces;
    private DispatcherTimer? _midnightTimer;

    public WelcomeBuddyIcon()
    {
        InitializeComponent();
        _faces = [Face0, Face1, Face2, Face3, Face4, Face5, Face6];
        ShowToday();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ArmMidnight();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_midnightTimer is null)
        {
            return;
        }

        _midnightTimer.Stop();
        _midnightTimer = null;
    }

    /// <summary>Monday = 0 … Sunday = 6, matching the web welcome icon.</summary>
    internal static int DayIndex(DateTime moment) => ((int)moment.DayOfWeek + 6) % 7;

    private void ShowToday()
    {
        if (_faces is null)
        {
            return;
        }

        var index = DayIndex(DateTime.Now);
        for (var i = 0; i < _faces.Length; i++)
        {
            _faces[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        AutomationProperties.SetName(this, $"Athlon {FaceNames[index]}");
    }

    private void ArmMidnight()
    {
        _midnightTimer?.Stop();
        var now = DateTime.Now;
        var delay = now.Date.AddDays(1) - now + TimeSpan.FromSeconds(1);
        _midnightTimer = new DispatcherTimer { Interval = delay };
        _midnightTimer.Tick += OnMidnight;
        _midnightTimer.Start();
    }

    private void OnMidnight(object? sender, EventArgs e)
    {
        ShowToday();
        ArmMidnight();
    }
}
