using System.Collections.ObjectModel;
using System.Windows.Threading;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core.Harness;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ComposerHarnessViewModel : ObservableObject
{
    private readonly ISessionHarnessState _harnessState;
    private readonly ISessionTaskListStore _taskListStore;
    private readonly ISessionPlanStore _planStore;
    private readonly ITaskPlanCompletionNotifier _taskPlanCompletionNotifier;
    private readonly ILocalizationService _loc;
    private string _sessionId = "";

    public ComposerHarnessViewModel(
        ISessionHarnessState harnessState,
        ISessionTaskListStore taskListStore,
        ISessionPlanStore planStore,
        ITaskPlanCompletionNotifier taskPlanCompletionNotifier,
        ILocalizationService localization)
    {
        _harnessState = harnessState;
        _taskListStore = taskListStore;
        _planStore = planStore;
        _taskPlanCompletionNotifier = taskPlanCompletionNotifier;
        _loc = localization;
        AppCultureManager.CultureChanged += OnCultureChanged;
    }

    public ObservableCollection<SessionTaskItemViewModel> Tasks { get; } = new();

    [ObservableProperty]
    private SessionAgentMode _selectedMode = SessionAgentMode.Agent;

    [ObservableProperty]
    private bool _isModePickerOpen;

    [ObservableProperty]
    private SessionPlan? _currentPlan;

    public bool IsHarnessActive => SelectedMode == SessionAgentMode.Coding;

    public bool IsPlanMode => SelectedMode == SessionAgentMode.Plan;

    public bool ShowTaskPanel => IsHarnessActive && Tasks.Count > 0;

    public bool ShowPlanPanel =>
        IsPlanMode
        && CurrentPlan is not null
        && CurrentPlan.HasContent
        && !string.Equals(CurrentPlan.Status, SessionPlanStatuses.Approved, StringComparison.OrdinalIgnoreCase);

    public bool CanConfirmPlan =>
        IsPlanMode
        && CurrentPlan is not null
        && string.Equals(
            SessionPlanStatuses.Normalize(CurrentPlan.Status),
            SessionPlanStatuses.AwaitingConfirmation,
            StringComparison.OrdinalIgnoreCase);

    public string HarnessButtonToolTip => SelectedMode switch
    {
        SessionAgentMode.Coding => _loc["Harness_Mode_Coding_Tooltip"],
        SessionAgentMode.Ask => _loc["Harness_Mode_Ask_Tooltip"],
        SessionAgentMode.Plan => _loc["Harness_Mode_Plan_Tooltip"],
        _ => _loc["Harness_Mode_Agent_Tooltip"],
    };

    public string HarnessPickerLabel => SelectedMode switch
    {
        SessionAgentMode.Coding => _loc["Harness_Mode_Coding"],
        SessionAgentMode.Ask => _loc["Harness_Mode_Ask"],
        SessionAgentMode.Plan => _loc["Harness_Mode_Plan"],
        _ => _loc["Harness_Mode_Agent"],
    };

    public int PendingTaskCount { get; private set; }

    public int InProgressTaskCount { get; private set; }

    /// <summary>Host starts a Coding turn after plan confirmation.</summary>
    public Func<Task>? OnPlanConfirmedAsync { get; set; }

    public Action? OnModePickerOpened { get; set; }

    public async Task LoadForSessionAsync(string sessionId)
    {
        if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            Tasks.Clear();
            CurrentPlan = null;
        }

        _sessionId = sessionId;
        await _harnessState.LoadAsync(sessionId).ConfigureAwait(true);
        SelectedMode = _harnessState.GetMode(sessionId);
        await RefreshTasksAsync().ConfigureAwait(true);
        await RefreshPlanAsync().ConfigureAwait(true);
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

        var wasCoding = SelectedMode == SessionAgentMode.Coding;
        await _harnessState.SaveAsync(_sessionId, new SessionHarnessSnapshot(mode)).ConfigureAwait(true);
        SelectedMode = mode;
        IsModePickerOpen = false;

        if (wasCoding && mode != SessionAgentMode.Coding)
        {
            ClearTasks();
        }
        else if (mode == SessionAgentMode.Coding)
        {
            await RefreshTasksAsync().ConfigureAwait(true);
        }

        if (mode == SessionAgentMode.Plan)
        {
            await RefreshPlanAsync().ConfigureAwait(true);
        }

        NotifyHarnessStateChanged();
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
    }

    public async Task RefreshPlanAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            CurrentPlan = null;
            OnPropertyChanged(nameof(ShowPlanPanel));
            OnPropertyChanged(nameof(CanConfirmPlan));
            ConfirmPlanCommand.NotifyCanExecuteChanged();
            return;
        }

        CurrentPlan = await _planStore.GetAsync(_sessionId).ConfigureAwait(true);
        OnPropertyChanged(nameof(ShowPlanPanel));
        OnPropertyChanged(nameof(CanConfirmPlan));
        ConfirmPlanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmPlan))]
    private async Task ConfirmPlanAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || CurrentPlan is null || !CanConfirmPlan)
        {
            return;
        }

        var plan = CurrentPlan;
        plan.Status = SessionPlanStatuses.Approved;
        await _planStore.SaveAsync(_sessionId, plan).ConfigureAwait(true);

        var seeded = new SessionTaskList
        {
            Items = plan.Todos.Select(todo => new AgentTaskItem
            {
                Id = todo.Id,
                Content = todo.Content,
                Status = AgentTaskStatuses.Pending
            }).ToList()
        };
        await _taskListStore.ReplaceAsync(_sessionId, seeded).ConfigureAwait(true);

        await _harnessState.SaveAsync(_sessionId, new SessionHarnessSnapshot(SessionAgentMode.Coding))
            .ConfigureAwait(true);
        SelectedMode = SessionAgentMode.Coding;
        await RefreshTasksAsync().ConfigureAwait(true);
        await RefreshPlanAsync().ConfigureAwait(true);
        NotifyHarnessStateChanged();

        if (OnPlanConfirmedAsync is not null)
        {
            await OnPlanConfirmedAsync().ConfigureAwait(true);
        }
    }

    public async Task ClearTaskPlanAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
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
        OnPropertyChanged(nameof(ShowPlanPanel));
        OnPropertyChanged(nameof(CanConfirmPlan));
        ConfirmPlanCommand.NotifyCanExecuteChanged();
    }

    private void NotifyHarnessStateChanged()
    {
        OnPropertyChanged(nameof(HarnessButtonToolTip));
        OnPropertyChanged(nameof(HarnessPickerLabel));
        OnPropertyChanged(nameof(ShowTaskPanel));
        OnPropertyChanged(nameof(IsHarnessActive));
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(ShowPlanPanel));
        OnPropertyChanged(nameof(CanConfirmPlan));
        ConfirmPlanCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModeChanged(SessionAgentMode value) => NotifyHarnessStateChanged();

    partial void OnCurrentPlanChanged(SessionPlan? value)
    {
        OnPropertyChanged(nameof(ShowPlanPanel));
        OnPropertyChanged(nameof(CanConfirmPlan));
        ConfirmPlanCommand.NotifyCanExecuteChanged();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        NotifyHarnessStateChanged();
        NotifyTaskCollectionChanged();
        foreach (var task in Tasks)
        {
            task.NotifyStatusFlagsChanged();
        }
    }
}

public sealed partial class SessionTaskItemViewModel : ObservableObject
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
}
