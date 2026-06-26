using System.Collections.ObjectModel;
using System.Windows.Threading;
using Athlon.Agent.Core.Harness;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ComposerHarnessViewModel : ObservableObject
{
    private readonly ISessionHarnessState _harnessState;
    private readonly ISessionTaskListStore _taskListStore;
    private string _sessionId = "";

    public ComposerHarnessViewModel(ISessionHarnessState harnessState, ISessionTaskListStore taskListStore)
    {
        _harnessState = harnessState;
        _taskListStore = taskListStore;
    }

    public ObservableCollection<SessionTaskItemViewModel> Tasks { get; } = new();

    [ObservableProperty]
    private bool _isHarnessActive;

    public bool ShowTaskPanel => IsHarnessActive && Tasks.Count > 0;

    public string HarnessButtonToolTip => IsHarnessActive
        ? "Coding 已启用 · 长期记忆 + 任务列表"
        : "启用 Coding（长期记忆与 todo_write）";

    public string HarnessPickerLabel => IsHarnessActive ? "Coding 开" : "Coding";

    public int PendingTaskCount { get; private set; }

    public int InProgressTaskCount { get; private set; }

    public async Task LoadForSessionAsync(string sessionId)
    {
        if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            Tasks.Clear();
        }

        _sessionId = sessionId;
        await _harnessState.LoadAsync(sessionId).ConfigureAwait(true);
        IsHarnessActive = _harnessState.IsEnabled(sessionId);
        await RefreshTasksAsync().ConfigureAwait(true);
        NotifyHarnessStateChanged();
    }

    [RelayCommand]
    private async Task ToggleHarnessAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        var next = !IsHarnessActive;
        await _harnessState.SaveAsync(_sessionId, new SessionHarnessSnapshot(next)).ConfigureAwait(true);
        IsHarnessActive = next;
        if (!next)
        {
            ClearTasks();
        }
        else
        {
            await RefreshTasksAsync().ConfigureAwait(true);
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
    }

    private void NotifyHarnessStateChanged()
    {
        OnPropertyChanged(nameof(HarnessButtonToolTip));
        OnPropertyChanged(nameof(HarnessPickerLabel));
        OnPropertyChanged(nameof(ShowTaskPanel));
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
        _ when IsInProgress => "进行中",
        _ when IsCompleted => "已完成",
        _ when IsCancelled => "已取消",
        _ => "待办",
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

    private void NotifyStatusFlagsChanged()
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsInProgress));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(StatusLabel));
    }
}
