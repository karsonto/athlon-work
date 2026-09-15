using System.Collections.ObjectModel;
using System.Windows.Threading;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ComposerHarnessViewModel : ObservableObject, IDisposable
{
    private readonly ISessionHarnessState _harnessState;
    private readonly ISessionTaskListStore _taskListStore;
    private readonly ITaskPlanCompletionNotifier _taskPlanCompletionNotifier;
    private readonly ILocalizationService _loc;
    private readonly AppSettings _appSettings;
    private readonly ISessionPlanArtifactsClearer? _planArtifactsClearer;
    private readonly IPlanContinuationTracker? _planContinuationTracker;
    private readonly IAppLogger? _logger;
    private string _sessionId = "";
    private bool _disposed;
    private IDisposable? _autoClearHandle;

    /// <summary>
    /// Delay before clearing a fully-completed plan. Longer than the 420ms task checkmark
    /// animation so the user sees the finished state, and roughly in step with the 3s
    /// completion notice auto-close.
    /// </summary>
    private static readonly TimeSpan AutoClearDelay = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Test seam: substitutes delayed-clear scheduling so the countdown can be driven without a
    /// dispatcher. Production leaves this null and a <see cref="DispatcherTimer"/> is used.
    /// </summary>
    internal Func<TimeSpan, Func<Task>, IDisposable>? AutoClearSchedulerOverride { get; }

    private static System.Windows.Threading.Dispatcher Dispatcher =>
        System.Windows.Application.Current?.Dispatcher
        ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

    public ComposerHarnessViewModel(
        ISessionHarnessState harnessState,
        ISessionTaskListStore taskListStore,
        ITaskPlanCompletionNotifier taskPlanCompletionNotifier,
        ILocalizationService localization,
        AppSettings appSettings,
        ISessionPlanArtifactsClearer? planArtifactsClearer,
        IPlanContinuationTracker? planContinuationTracker,
        IAppLogger? logger,
        Func<TimeSpan, Func<Task>, IDisposable>? autoClearScheduler = null)
    {
        _harnessState = harnessState;
        _taskListStore = taskListStore;
        _taskPlanCompletionNotifier = taskPlanCompletionNotifier;
        _loc = localization;
        _appSettings = appSettings;
        _planArtifactsClearer = planArtifactsClearer;
        _planContinuationTracker = planContinuationTracker;
        _logger = logger?.ForContext("ComposerHarnessViewModel");
        AutoClearSchedulerOverride = autoClearScheduler;
        AppCultureManager.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// Test/design-time constructor: keeps the pre-clearing behavior (no plan artifact clearing)
    /// so existing harness tests do not need to fake the whole App-layer clearer.
    /// </summary>
    public ComposerHarnessViewModel(
        ISessionHarnessState harnessState,
        ISessionTaskListStore taskListStore,
        ITaskPlanCompletionNotifier taskPlanCompletionNotifier,
        ILocalizationService localization)
        : this(
            harnessState,
            taskListStore,
            taskPlanCompletionNotifier,
            localization,
            new AppSettings(),
            planArtifactsClearer: null,
            planContinuationTracker: null,
            logger: null,
            autoClearScheduler: null)
    {
    }

    public ObservableCollection<SessionTaskItemViewModel> Tasks { get; } = new();

    [ObservableProperty]
    private SessionAgentMode _selectedMode = SessionAgentMode.Agent;

    [ObservableProperty]
    private bool _isModePickerOpen;

    public bool IsHarnessActive => SelectedMode == SessionAgentMode.Agent;

    public bool IsPlanMode => SelectedMode == SessionAgentMode.Plan;

    public bool IsDebugMode => SelectedMode == SessionAgentMode.Debug;

    public bool ShowTaskPanel => IsHarnessActive && Tasks.Count > 0;

    public string HarnessButtonToolTip => SelectedMode switch
    {
        SessionAgentMode.Ask => _loc["Harness_Mode_Ask_Tooltip"],
        SessionAgentMode.Plan => _loc["Harness_Mode_Plan_Tooltip"],
        SessionAgentMode.Debug => _loc["Harness_Mode_Debug_Tooltip"],
        _ => _loc["Harness_Mode_Agent_Tooltip"],
    };

    public string HarnessPickerLabel => SelectedMode switch
    {
        SessionAgentMode.Ask => _loc["Harness_Mode_Ask"],
        SessionAgentMode.Plan => _loc["Harness_Mode_Plan"],
        SessionAgentMode.Debug => _loc["Harness_Mode_Debug"],
        _ => _loc["Harness_Mode_Agent"],
    };

    public int PendingTaskCount { get; private set; }

    public int InProgressTaskCount { get; private set; }

    /// <summary>
    /// Whether an approved plan keeps running automatically when a turn ends with open tasks.
    /// Persisted in app settings; flipping it takes effect on the next turn boundary.
    /// </summary>
    public bool IsAutoContinueEnabled
    {
        get => _appSettings.AgentTurn.ResolvePlanAutoContinueEnabled();
        set
        {
            if (_appSettings.AgentTurn.PlanAutoContinueEnabled == value)
            {
                return;
            }

            _appSettings.AgentTurn.PlanAutoContinueEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStopAutoContinue));
            // Best-effort persistence: the toggle is an app setting, not session state.
            _ = OnAutoContinueSettingChangedAsync?.Invoke();
        }
    }

    /// <summary>True when the stop button is worth showing: auto-continue is on and not already stopped.</summary>
    public bool CanStopAutoContinue
    {
        get
        {
            if (_planContinuationTracker is null || string.IsNullOrWhiteSpace(_sessionId))
            {
                return false;
            }

            return IsAutoContinueEnabled
                && !_planContinuationTracker.IsStopped(_sessionId)
                && (_planContinuationTracker.GetCount(_sessionId) > 0
                    || InProgressTaskCount > 0
                    || PendingTaskCount > 0);
        }
    }

    /// <summary>
    /// Stops automatic continuation for the displayed session. The plan and tasks stay put; the
    /// user can keep driving manually, and the next Build resets the budget.
    /// </summary>
    [RelayCommand]
    private void StopAutoContinue()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || _planContinuationTracker is null)
        {
            return;
        }

        _planContinuationTracker.Stop(_sessionId);
        OnPropertyChanged(nameof(CanStopAutoContinue));
    }

    public Func<SessionAgentMode, SessionAgentMode, Task>? OnModeChangedAsync { get; set; }

    public Action? OnModePickerOpened { get; set; }

    /// <summary>
    /// Raised after a fully-completed plan was archived. The shell uses it to tell the user the
    /// task list and plan artifact are gone, so their disappearance is not a surprise.
    /// </summary>
    public Action<string>? OnPlanAutoCleared { get; set; }

    /// <summary>Persists the auto-continue toggle; the shell saves app settings on our behalf.</summary>
    public Func<Task>? OnAutoContinueSettingChangedAsync { get; set; }

    public async Task LoadForSessionAsync(string sessionId)
    {
        if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            Tasks.Clear();
            CancelAutoClearTimer();
        }

        _sessionId = sessionId;
        await _harnessState.LoadAsync(sessionId).ConfigureAwait(true);
        SelectedMode = _harnessState.GetMode(sessionId);
        await RefreshTasksAsync().ConfigureAwait(true);
        NotifyHarnessStateChanged();
    }

    [RelayCommand]
    private void ToggleModePicker()
    {
        IsModePickerOpen = !IsModePickerOpen;
        if (IsModePickerOpen)
        {
            OnModePickerOpened?.Invoke();
        }
    }

    [RelayCommand]
    private async Task SelectModeAsync(SessionAgentMode mode)
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || SelectedMode == mode)
        {
            IsModePickerOpen = false;
            return;
        }

        var previous = SelectedMode;
        await _harnessState.SaveAsync(_sessionId, new SessionHarnessSnapshot(mode)).ConfigureAwait(true);
        SelectedMode = mode;
        IsModePickerOpen = false;

        if (previous == SessionAgentMode.Agent && mode != SessionAgentMode.Agent)
        {
            // Only reclaim the list when execution has not started. Once any task is in progress or
            // completed, the list represents real work under an approved plan; switching to Plan/Ask
            // to look something up must not destroy it.
            var list = await _taskListStore.GetAsync(_sessionId).ConfigureAwait(true);
            if (IsUnstarted(list))
            {
                // Route through the unified clearer so a never-started plan's disk artifacts and
                // phase go away with the list, instead of leaving a plan.md to be re-injected.
                await ClearTaskPlanAsync().ConfigureAwait(true);
            }
        }
        else if (mode == SessionAgentMode.Agent)
        {
            await RefreshTasksAsync().ConfigureAwait(true);
        }

        NotifyHarnessStateChanged();
        if (OnModeChangedAsync is not null)
        {
            await OnModeChangedAsync(previous, mode).ConfigureAwait(true);
        }
    }

    public async Task RefreshTasksAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || !IsHarnessActive)
        {
            ClearTasks();
            PendingTaskCount = 0;
            InProgressTaskCount = 0;
            OnPropertyChanged(nameof(PendingTaskCount));
            OnPropertyChanged(nameof(InProgressTaskCount));
            return;
        }

        var list = await _taskListStore.GetAsync(_sessionId).ConfigureAwait(true);
        var incomingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byId = Tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var item in list.Items)
        {
            incomingIds.Add(item.Id);
            if (byId.TryGetValue(item.Id, out var existing))
            {
                var wasCompleted = existing.IsCompleted;
                existing.UpdateFrom(item);
                if (!wasCompleted && existing.IsCompleted)
                {
                    existing.TriggerCompletionAnimation();
                    _taskPlanCompletionNotifier.NotifyTaskCompleted(existing.Content);
                }
            }
            else
            {
                Tasks.Add(new SessionTaskItemViewModel(item));
            }
        }

        for (var index = Tasks.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(Tasks[index].Id))
            {
                Tasks.RemoveAt(index);
            }
        }

        PendingTaskCount = list.Items.Count(i =>
            string.Equals(i.Status, AgentTaskStatuses.Pending, StringComparison.OrdinalIgnoreCase));
        InProgressTaskCount = list.Items.Count(i =>
            string.Equals(i.Status, AgentTaskStatuses.InProgress, StringComparison.OrdinalIgnoreCase));

        NotifyTaskCollectionChanged();
        ScheduleAutoClearIfAllCompleted(list);
    }

    /// <summary>
    /// Schedules a delayed clear once every task is completed. The delay exists so the user
    /// actually sees the completion state (checkmark animation plus the completion notice)
    /// before the panel and plan card disappear.
    /// </summary>
    private void ScheduleAutoClearIfAllCompleted(SessionTaskList list)
    {
        var enabled = _planArtifactsClearer is not null
            && _appSettings.AgentTurn.ResolveClearPlanOnAllTasksCompleted();
        if (!enabled || string.IsNullOrWhiteSpace(_sessionId) || !IsAllCompleted(list))
        {
            return;
        }

        // Already counting down; the callback re-verifies before clearing.
        if (_autoClearHandle is not null)
        {
            return;
        }

        var sessionId = _sessionId;
        _autoClearHandle = ScheduleAutoClear(AutoClearDelay, async () =>
        {
            CancelAutoClearTimer();
            await TryAutoClearCompletedPlanAsync(sessionId).ConfigureAwait(true);
        });
    }

    private IDisposable ScheduleAutoClear(TimeSpan delay, Func<Task> action)
    {
        if (AutoClearSchedulerOverride is not null)
        {
            return AutoClearSchedulerOverride(delay, action);
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = delay };
        timer.Tick += async (_, _) => await action().ConfigureAwait(true);
        timer.Start();
        return new DispatcherTimerHandle(timer);
    }

    private async Task TryAutoClearCompletedPlanAsync(string sessionId)
    {
        if (_planArtifactsClearer is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            // Re-verify: the model may have written new todos during the delay, and clearing then
            // would delete work the user just queued up.
            var list = await _taskListStore.GetAsync(sessionId).ConfigureAwait(true);
            if (!IsAllCompleted(list))
            {
                return;
            }

            await _planArtifactsClearer.ClearAsync(sessionId).ConfigureAwait(true);
            OnPlanAutoCleared?.Invoke(sessionId);
        }
        catch (Exception ex)
        {
            _logger?.Warning("Failed to auto-clear completed plan for session {SessionId}: {Error}", sessionId, ex.Message);
        }
    }

    private void CancelAutoClearTimer()
    {
        var handle = _autoClearHandle;
        _autoClearHandle = null;
        handle?.Dispose();
    }

    /// <summary>Adapter so the dispatcher-backed timer shares the <see cref="IDisposable"/> handle shape.</summary>
    private sealed class DispatcherTimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose()
        {
            timer.Stop();
        }
    }

    /// <summary>Every item completed, and the list is non-empty.</summary>
    private static bool IsAllCompleted(SessionTaskList list) =>
        list.Items.Count > 0
        && list.Items.All(item =>
            string.Equals(item.Status, AgentTaskStatuses.Completed, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when no work has started yet, so the list can be discarded on a mode switch.</summary>
    private static bool IsUnstarted(SessionTaskList list) =>
        list.Items.All(item =>
            !string.Equals(item.Status, AgentTaskStatuses.InProgress, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.Status, AgentTaskStatuses.Completed, StringComparison.OrdinalIgnoreCase));

    public async Task ClearTaskPlanAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        CancelAutoClearTimer();

        if (_planArtifactsClearer is not null)
        {
            await _planArtifactsClearer.ClearAsync(_sessionId).ConfigureAwait(true);
            Tasks.Clear();
            PendingTaskCount = 0;
            InProgressTaskCount = 0;
            NotifyTaskCollectionChanged();
            OnPropertyChanged(nameof(CanStopAutoContinue));
            return;
        }

        await _taskListStore.ReplaceAsync(_sessionId, new SessionTaskList()).ConfigureAwait(true);
        Tasks.Clear();
        PendingTaskCount = 0;
        InProgressTaskCount = 0;
        NotifyTaskCollectionChanged();
    }

    private void ClearTasks()
    {
        Tasks.Clear();
        OnPropertyChanged(nameof(ShowTaskPanel));
    }

    private void NotifyTaskCollectionChanged()
    {
        OnPropertyChanged(nameof(ShowTaskPanel));
        OnPropertyChanged(nameof(PendingTaskCount));
        OnPropertyChanged(nameof(InProgressTaskCount));
        OnPropertyChanged(nameof(HarnessButtonToolTip));
        OnPropertyChanged(nameof(HarnessPickerLabel));
        OnPropertyChanged(nameof(IsHarnessActive));
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(IsDebugMode));
        // The stop button's visibility depends on whether open work remains, so it follows the
        // task list rather than the turn lifecycle.
        OnPropertyChanged(nameof(CanStopAutoContinue));
    }

    private void NotifyHarnessStateChanged()
    {
        OnPropertyChanged(nameof(HarnessButtonToolTip));
        OnPropertyChanged(nameof(HarnessPickerLabel));
        OnPropertyChanged(nameof(ShowTaskPanel));
        OnPropertyChanged(nameof(IsHarnessActive));
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(IsDebugMode));
    }

    partial void OnSelectedModeChanged(SessionAgentMode value) => NotifyHarnessStateChanged();

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        NotifyHarnessStateChanged();
        NotifyTaskCollectionChanged();
        foreach (var task in Tasks)
        {
            task.NotifyStatusFlagsChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAutoClearTimer();
        AppCultureManager.CultureChanged -= OnCultureChanged;
        foreach (var task in Tasks)
        {
            task.Dispose();
        }
    }
}

public sealed partial class SessionTaskItemViewModel : ObservableObject, IDisposable
{
    private DispatcherTimer? _completionAnimationTimer;

    public SessionTaskItemViewModel(AgentTaskItem item)
    {
        Id = item.Id;
        UpdateFrom(item);
    }

    public string Id { get; }

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private string _status = AgentTaskStatuses.Pending;

    [ObservableProperty]
    private bool _shouldPlayCompletionAnimation;

    public bool IsPending =>
        string.Equals(Status, AgentTaskStatuses.Pending, StringComparison.OrdinalIgnoreCase);

    public bool IsInProgress =>
        string.Equals(Status, AgentTaskStatuses.InProgress, StringComparison.OrdinalIgnoreCase);

    public bool IsCompleted =>
        string.Equals(Status, AgentTaskStatuses.Completed, StringComparison.OrdinalIgnoreCase);

    public bool IsCancelled =>
        string.Equals(Status, AgentTaskStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);

    public string StatusLabel => Status switch
    {
        _ when IsInProgress => Strings.Get("Harness_TaskInProgress"),
        _ when IsCompleted => Strings.Get("Harness_TaskCompleted"),
        _ when IsCancelled => Strings.Get("Harness_TaskCancelled"),
        _ => Strings.Get("Harness_TaskPending"),
    };

    public void UpdateFrom(AgentTaskItem item)
    {
        Content = item.Content;
        Status = AgentTaskStatuses.Normalize(item.Status);
        NotifyStatusFlagsChanged();
    }

    public void TriggerCompletionAnimation()
    {
        ShouldPlayCompletionAnimation = true;
        _completionAnimationTimer?.Stop();
        _completionAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420),
        };
        _completionAnimationTimer.Tick += OnCompletionAnimationTimerTick;
        _completionAnimationTimer.Start();
    }

    partial void OnStatusChanged(string value) => NotifyStatusFlagsChanged();

    private void OnCompletionAnimationTimerTick(object? sender, EventArgs e)
    {
        if (_completionAnimationTimer is not null)
        {
            _completionAnimationTimer.Tick -= OnCompletionAnimationTimerTick;
            _completionAnimationTimer.Stop();
            _completionAnimationTimer = null;
        }

        ShouldPlayCompletionAnimation = false;
    }

    internal void NotifyStatusFlagsChanged()
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsInProgress));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(StatusLabel));
    }

    public void Dispose()
    {
        if (_completionAnimationTimer is not null)
        {
            _completionAnimationTimer.Tick -= OnCompletionAnimationTimerTick;
            _completionAnimationTimer.Stop();
            _completionAnimationTimer = null;
        }
    }
}
