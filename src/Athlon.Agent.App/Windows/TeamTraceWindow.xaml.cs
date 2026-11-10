using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Athlon.Agent.App.Behaviors;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core.Teams;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Windows;

public partial class TeamTraceWindow : Window
{
    private static TeamTraceWindow? _singleton;
    private TeamTask _task;
    private IReadOnlyList<TeamTraceEvent> _events;
    private ITeamTraceStore? _traceStore;
    private ITeamTaskStore? _taskStore;
    private string? _teamName;
    private bool _live;
    private DispatcherTimer? _pollTimer;
    private bool _webReady;
    private bool _pollInFlight;
    private string? _lastFingerprint;

    private TeamTraceWindow(
        TeamTask task,
        IReadOnlyList<TeamTraceEvent> events,
        ITeamTraceStore? traceStore,
        string? teamName,
        ITeamTaskStore? taskStore,
        bool live)
    {
        _task = task;
        _events = events;
        _traceStore = traceStore;
        _teamName = teamName;
        _taskStore = taskStore;
        _live = live;
        InitializeComponent();
        MaximizedWindowWorkArea.Attach(this);
        ApplyChromeTitle(teamName, task);
        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += (_, _) => UpdateMaximizeRestoreButton();
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
        EnsurePollTimer();
        UpdateMaximizeRestoreButton();
    }

    public static void ShowOrFocus(
        TeamTask task,
        IReadOnlyList<TeamTraceEvent> events,
        Window? owner,
        ITeamTraceStore? traceStore = null,
        string? teamName = null,
        ITeamTaskStore? taskStore = null,
        bool live = false)
    {
        if (_singleton is { IsLoaded: true, _webReady: true } existing
            && existing.TraceWebView.CoreWebView2 is not null)
        {
            existing.Adopt(task, events, traceStore, teamName, taskStore, live);
            if (owner is not null && existing.Owner is null)
            {
                existing.Owner = owner;
            }

            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Maximized;
            }

            existing.Show();
            existing.Activate();
            return;
        }

        if (_singleton is { IsLoaded: true })
        {
            _singleton.Close();
            _singleton = null;
        }

        var window = new TeamTraceWindow(task, events, traceStore, teamName, taskStore, live);
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

    private void Adopt(
        TeamTask task,
        IReadOnlyList<TeamTraceEvent> events,
        ITeamTraceStore? traceStore,
        string? teamName,
        ITeamTaskStore? taskStore,
        bool live)
    {
        _task = task;
        _events = events;
        _traceStore = traceStore;
        _teamName = teamName;
        _taskStore = taskStore;
        _live = live;
        _lastFingerprint = null;
        ApplyChromeTitle(teamName, task);
        EnsurePollTimer();
        _ = PushPayloadAsync(force: true);
        if (_live)
        {
            _pollTimer?.Start();
        }
        else
        {
            _pollTimer?.Stop();
        }
    }

    private void ApplyChromeTitle(string? teamName, TeamTask task)
    {
        var label = string.IsNullOrWhiteSpace(teamName) ? task.TeamId : teamName;
        Title = $"Team Trace · {label}";
        TitleText.Text = Title;
    }

    private void EnsurePollTimer()
    {
        if (_pollTimer is not null || !_live || _taskStore is null || _traceStore is null)
        {
            return;
        }

        // Sub-second polling so Worker Tools appear while tools are still in flight.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += async (_, _) => await PollLiveAsync().ConfigureAwait(true);
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

            // NavigateToString is async: pushing Tools before NavigationCompleted can no-op
            // applyTraceUpdate while still stamping _lastFingerprint, so reopen stays empty.
            var navigationDone = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? _, CoreWebView2NavigationCompletedEventArgs args)
            {
                core.NavigationCompleted -= OnNavigationCompleted;
                navigationDone.TrySetResult(args.IsSuccess);
            }

            core.NavigationCompleted += OnNavigationCompleted;

            // Bake transcripts into the first document so close/reopen does not depend solely on a
            // post-navigation script push (which used to race and leave Tools empty).
            var task = _task;
            var events = _events;
            var teamName = _teamName;
            var taskStore = _taskStore;
            var initial = await Task.Run(() =>
            {
                var transcripts = taskStore is null
                    ? null
                    : TeamTraceTranscriptBuilder.Build(task, taskStore);
                var fingerprint = taskStore is null
                    ? null
                    : TeamTraceTranscriptBuilder.ComputeFingerprint(task, events, taskStore);
                var html = TeamTraceHtml.Build(task, events, teamName, transcripts);
                return (html, fingerprint);
            }).ConfigureAwait(true);

            core.NavigateToString(initial.html);

            var navigated = await navigationDone.Task.ConfigureAwait(true);
            if (!navigated)
            {
                throw new InvalidOperationException("Team Trace page failed to load.");
            }

            _webReady = true;
            if (!string.IsNullOrWhiteSpace(initial.fingerprint))
            {
                _lastFingerprint = initial.fingerprint;
            }

            if (_live)
            {
                _pollTimer?.Start();
            }

            // Refresh once more in case member files changed while the page was loading.
            await PushPayloadAsync(force: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Team Trace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        _pollTimer?.Stop();
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        if (!_webReady || TraceWebView.CoreWebView2 is null)
        {
            return;
        }

        var theme = TeamTraceHtml.CurrentThemeAttribute();
        _ = TraceWebView.CoreWebView2.ExecuteScriptAsync(
            $"document.documentElement.setAttribute('data-theme','{theme}');");
    }

    private async Task PollLiveAsync()
    {
        if (!_webReady || _pollInFlight || _taskStore is null || _traceStore is null)
        {
            return;
        }

        if (TraceWebView.CoreWebView2 is null)
        {
            return;
        }

        _pollInFlight = true;
        try
        {
            var taskStore = _taskStore;
            var traceStore = _traceStore;
            var rootSessionId = _task.RootSessionId;
            var taskId = _task.TaskId;

            var snapshot = await Task.Run(async () =>
            {
                var task = await taskStore.GetAsync(rootSessionId, taskId).ConfigureAwait(false);
                if (task is null)
                {
                    return ((TeamTask?)null, (IReadOnlyList<TeamTraceEvent>)Array.Empty<TeamTraceEvent>(), (string?)null,
                        (IReadOnlyDictionary<string, IReadOnlyList<object>>?)null);
                }

                var events = await traceStore.ReadAsync(task.RootSessionId, task.TaskId).ConfigureAwait(false);
                var fingerprint = TeamTraceTranscriptBuilder.ComputeFingerprint(task, events, taskStore);
                return (task, events, fingerprint, (IReadOnlyDictionary<string, IReadOnlyList<object>>?)null);
            }).ConfigureAwait(true);

            if (snapshot.Item1 is null)
            {
                return;
            }

            var task = snapshot.Item1;
            var events = snapshot.Item2;
            var fingerprint = snapshot.Item3;
            _task = task;
            _events = events;

            if (string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
            {
                if (IsTerminal(task.Status))
                {
                    _pollTimer?.Stop();
                }

                return;
            }

            await PushPayloadAsync(force: false, fingerprint).ConfigureAwait(true);

            if (IsTerminal(task.Status))
            {
                _pollTimer?.Stop();
            }
        }
        catch
        {
            // Keep polling; transient load failures during member writes are expected.
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private async Task PushPayloadAsync(bool force, string? knownFingerprint = null)
    {
        if (!_webReady || TraceWebView.CoreWebView2 is null || _taskStore is null)
        {
            return;
        }

        var task = _task;
        var events = _events;
        var teamName = _teamName;
        var taskStore = _taskStore;

        var built = await Task.Run(() =>
        {
            var fingerprint = knownFingerprint
                ?? TeamTraceTranscriptBuilder.ComputeFingerprint(task, events, taskStore);
            var transcripts = TeamTraceTranscriptBuilder.Build(task, taskStore);
            // Avoid serializing the same leader transcript three times.
            if (transcripts.TryGetValue("leader", out var leader)
                && transcripts.ContainsKey("agg")
                && transcripts.ContainsKey("submit"))
            {
                // Keep aliases; payload size is still fine for typical runs.
            }

            var json = TeamTraceHtml.BuildPayloadJson(task, events, teamName, transcripts);
            return (fingerprint, json);
        }).ConfigureAwait(true);

        if (!force && string.Equals(built.fingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        // Only stamp fingerprint after the page actually applied the payload. Otherwise a race
        // against NavigateToString leaves Tools empty and every later poll/push is skipped.
        var appliedJson = await TraceWebView.CoreWebView2
            .ExecuteScriptAsync(
                "(function(){ try { if (typeof window.applyTraceUpdate !== 'function') return false; "
                + $"window.applyTraceUpdate({built.json}); return true; "
                + "} catch (e) { return false; } })()")
            .ConfigureAwait(true);
        if (string.Equals(appliedJson, "true", StringComparison.Ordinal))
        {
            _lastFingerprint = built.fingerprint;
        }
    }

    private static bool IsTerminal(TeamTaskStatus status) =>
        status is TeamTaskStatus.Succeeded or TeamTaskStatus.Failed or TeamTaskStatus.Cancelled;

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

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            return;
        }

        DragMove();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e) =>
        ToggleWindowState();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowState() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeRestoreButton()
    {
        if (MaximizeRestoreButton is null)
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = null;
        MaximizeRestoreButton.ContentTemplate = (DataTemplate)MaximizeRestoreButton.FindResource(
            isMaximized ? "WindowCaptionRestoreIconTemplate" : "WindowCaptionMaximizeIconTemplate");
        MaximizeRestoreButton.ToolTip = isMaximized
            ? Strings.Get("Common_Restore")
            : Strings.Get("Common_Maximize");
    }
}
