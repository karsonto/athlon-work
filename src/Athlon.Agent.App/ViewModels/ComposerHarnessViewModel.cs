using System.Collections.ObjectModel;
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

    public ObservableCollection<ComposerHarnessTaskChipViewModel> ActiveTasks { get; } = new();

    [ObservableProperty]
    private bool _isHarnessActive;

    public bool ShowTaskChips => IsHarnessActive && ActiveTasks.Count > 0;

    public string HarnessButtonToolTip => IsHarnessActive
        ? "Harness 已启用 · 长期记忆 + 任务列表"
        : "启用 Harness（长期记忆与 todo_write）";

    public string HarnessPickerLabel => IsHarnessActive ? "Harness 开" : "Harness";

    public int PendingTaskCount { get; private set; }

    public int InProgressTaskCount { get; private set; }

    public async Task LoadForSessionAsync(string sessionId)
    {
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
            ActiveTasks.Clear();
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
            ActiveTasks.Clear();
            PendingTaskCount = 0;
            InProgressTaskCount = 0;
            OnPropertyChanged(nameof(ShowTaskChips));
            return;
        }

        var list = await _taskListStore.GetAsync(_sessionId).ConfigureAwait(true);
        ActiveTasks.Clear();
        foreach (var item in list.Items.Where(IsActiveTask))
        {
            ActiveTasks.Add(new ComposerHarnessTaskChipViewModel(item));
        }

        PendingTaskCount = list.Items.Count(i =>
            string.Equals(i.Status, AgentTaskStatuses.Pending, StringComparison.OrdinalIgnoreCase));
        InProgressTaskCount = list.Items.Count(i =>
            string.Equals(i.Status, AgentTaskStatuses.InProgress, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(ShowTaskChips));
        OnPropertyChanged(nameof(PendingTaskCount));
        OnPropertyChanged(nameof(InProgressTaskCount));
        OnPropertyChanged(nameof(HarnessButtonToolTip));
        OnPropertyChanged(nameof(HarnessPickerLabel));
    }

    private static bool IsActiveTask(AgentTaskItem item) =>
        string.Equals(item.Status, AgentTaskStatuses.Pending, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.Status, AgentTaskStatuses.InProgress, StringComparison.OrdinalIgnoreCase);

    private void NotifyHarnessStateChanged()
    {
        OnPropertyChanged(nameof(HarnessButtonToolTip));
        OnPropertyChanged(nameof(HarnessPickerLabel));
        OnPropertyChanged(nameof(ShowTaskChips));
    }
}

public sealed class ComposerHarnessTaskChipViewModel(AgentTaskItem item)
{
    public string Id { get; } = item.Id;
    public string Content { get; } = item.Content;
    public string Status { get; } = item.Status;
    public string StatusLabel => string.Equals(Status, AgentTaskStatuses.InProgress, StringComparison.OrdinalIgnoreCase)
        ? "进行中"
        : "待办";
}
