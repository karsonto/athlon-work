using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core.Teams;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Windows;

public partial class TeamTraceWindow : Window
{
    private static TeamTraceWindow? _singleton;
    private readonly TeamTask _task;
    private readonly IReadOnlyList<TeamTraceEvent> _events;
    private readonly ITeamTraceStore? _traceStore;
    private readonly string? _teamName;

    private TeamTraceWindow(
        TeamTask task,
        IReadOnlyList<TeamTraceEvent> events,
        ITeamTraceStore? traceStore,
        string? teamName)
    {
        _task = task;
        _events = events;
        _traceStore = traceStore;
        _teamName = teamName;
        InitializeComponent();
        var label = string.IsNullOrWhiteSpace(teamName) ? task.TeamId : teamName;
        Title = $"Team Trace · {label}";
        TitleText.Text = Title;
        Loaded += OnLoaded;
    }

    public static void ShowOrFocus(
        TeamTask task,
        IReadOnlyList<TeamTraceEvent> events,
        Window? owner,
        ITeamTraceStore? traceStore = null,
        string? teamName = null)
    {
        if (_singleton is { IsLoaded: true })
        {
            _singleton.Close();
            _singleton = null;
        }

        var window = new TeamTraceWindow(task, events, traceStore, teamName);
        _singleton = window;
        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_singleton, window))
            {
                _singleton = null;
            }
        };
        window.Show();
        window.Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView2Initializer.EnsureCoreWebView2Async(TraceWebView).ConfigureAwait(true);
            var core = TraceWebView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 failed to initialize.");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigateToString(TeamTraceHtml.Build(_task, _events, _teamName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Team Trace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
            {
                return;
            }

            var type = typeEl.GetString();
            if (string.Equals(type, "close", StringComparison.Ordinal))
            {
                Dispatcher.Invoke(Close);
                return;
            }

            if (!string.Equals(type, "traceLayout", StringComparison.Ordinal) || _traceStore is null)
            {
                return;
            }

            if (!doc.RootElement.TryGetProperty("layout", out var layoutEl))
            {
                return;
            }

            var layout = new Dictionary<string, TeamTraceNodeLayout>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in layoutEl.EnumerateObject())
            {
                var x = prop.Value.TryGetProperty("x", out var xEl) ? xEl.GetDouble() : 0;
                var y = prop.Value.TryGetProperty("y", out var yEl) ? yEl.GetDouble() : 0;
                layout[prop.Name] = new TeamTraceNodeLayout { X = x, Y = y };
            }

            await _traceStore.SaveLayoutAsync(_task.RootSessionId, _task.TaskId, layout).ConfigureAwait(true);
        }
        catch
        {
            // ignore malformed host messages
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
