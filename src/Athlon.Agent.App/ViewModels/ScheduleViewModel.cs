using System.Collections.ObjectModel;
using System.Windows;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ScheduleViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IFileStorageService _storage;
    private readonly SchedulerService _scheduler;
    private readonly ILocalizationService _loc;
    private readonly IUserNotifier _notifier;
    private readonly Func<string, Task>? _openSession;
    private bool _isSyncing;

    public ScheduleViewModel(
        AppSettings settings,
        IFileStorageService storage,
        SchedulerService scheduler,
        Lazy<ISessionHost> sessionHost,
        ILocalizationService localization,
        IUserNotifier notifier)
    {
        _settings = settings;
        _storage = storage;
        _scheduler = scheduler;
        _loc = localization;
        _notifier = notifier;
        _openSession = id => sessionHost.Value.OpenSessionByIdAsync(id);
        _scheduler.TaskStatusChanged += OnTaskStatusChanged;
        AppCultureManager.CultureChanged += OnCultureChanged;
        InitializeFilterChips();
        SyncFromSettings();
        UpdateSchedulerState();
    }

    public ObservableCollection<ScheduleTaskItemViewModel> Tasks { get; } = new();

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private string taskSummary = "";

    [ObservableProperty]
    private int selectedFilterIndex;

    [ObservableProperty]
    private bool hasTasks;

    [ObservableProperty]
    private bool isSchedulerRunning;

    public ObservableCollection<ScheduleFilterChipViewModel> FilterChips { get; } = new();

    partial void OnIsEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _settings.Schedule.Enabled = value;
        UpdateSchedulerState();
        _ = PersistAsync();
    }

    partial void OnSelectedFilterIndexChanged(int value)
    {
        UpdateFilterChipSelection();
        ApplyFilter();
    }

    private void InitializeFilterChips()
    {
        FilterChips.Clear();
        FilterChips.Add(new ScheduleFilterChipViewModel(_loc["Schedule_FilterAll"], 0) { IsSelected = true });
        FilterChips.Add(new ScheduleFilterChipViewModel(_loc["Schedule_FilterEnabled"], 1));
        FilterChips.Add(new ScheduleFilterChipViewModel(_loc["Schedule_FilterRunning"], 2));
        FilterChips.Add(new ScheduleFilterChipViewModel(_loc["Schedule_FilterCompleted"], 3));
    }

    private void UpdateFilterChipSelection()
    {
        foreach (var chip in FilterChips)
        {
            chip.IsSelected = chip.Index == SelectedFilterIndex;
        }
    }

    public void SyncFromSettings()
    {
        _isSyncing = true;
        try
        {
            Tasks.Clear();
            foreach (var task in _settings.Schedule.Tasks)
            {
                Tasks.Add(CreateTaskItem(task));
            }

            RefreshState();
            ApplyFilter();
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private ScheduleTaskItemViewModel CreateTaskItem(ScheduledTask task) =>
        new(
            task,
            _settings,
            _storage,
            _scheduler,
            _loc,
            _notifier,
            onDeleted: HandleTaskDeleted,
            openSession: _openSession);

    private void HandleTaskDeleted(ScheduleTaskItemViewModel item)
    {
        Tasks.Remove(item);
        RefreshState();
        ApplyFilter();
    }

    private void RefreshState()
    {
        var enabledCount = Tasks.Count(t => t.Enabled);
        TaskSummary = HasTasks
            ? _loc.Format("Schedule_TaskSummary", Tasks.Count, enabledCount)
            : _loc["Schedule_NoTasks"];
        IsEnabled = _settings.Schedule.Enabled;
        HasTasks = Tasks.Count > 0;
        IsSchedulerRunning = _scheduler.IsRunning;
    }

    private void ApplyFilter()
    {
        var filter = SelectedFilterIndex switch
        {
            1 => "enabled",
            2 => "running",
            3 => "success",
            _ => null
        };

        foreach (var item in Tasks)
        {
            item.IsVisible = filter switch
            {
                null => true,
                "enabled" => item.Enabled,
                "running" => item.IsRunning,
                "success" => item.LastStatus == "success",
                _ => true
            };
        }
    }

    private void OnTaskStatusChanged(object? sender, ScheduledTaskStatusEventArgs e)
    {
        var match = Tasks.FirstOrDefault(t => t.Id == e.TaskId);
        if (match is not null)
        {
            match.Refresh();
            ApplyFilter();
        }
        RefreshState();
    }

    private void UpdateSchedulerState()
    {
        if (_settings.Schedule.Enabled)
        {
            _scheduler.Start();
        }
        else
        {
            _scheduler.Stop();
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            await _storage.SaveSettingsAsync(_settings);
        }
        catch { }
    }

    [RelayCommand]
    private void SetFilter(ScheduleFilterChipViewModel? chip)
    {
        if (chip is null)
        {
            return;
        }

        SelectedFilterIndex = chip.Index;
    }

    [RelayCommand]
    private void NewTask()
    {
        var task = new ScheduledTask
        {
            Title = _loc["Schedule_NewTaskDefaultTitle"],
            Kind = "daily",
            TimeOfDay = "09:00",
            Prompt = "",
            Mode = "agent",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };

        var window = new ScheduleTaskEditWindow(task, _notifier, _loc, isNew: true);
        window.Owner = Application.Current.MainWindow;
        if (window.ShowDialog() != true)
        {
            return;
        }

        ScheduleTiming.EnsureNextRunAt(task);
        _settings.Schedule.Tasks.Add(task);
        Tasks.Add(CreateTaskItem(task));
        RefreshState();
        ApplyFilter();
        _ = PersistAsync();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        InitializeFilterChips();
        UpdateFilterChipSelection();
        RefreshState();
        foreach (var task in Tasks)
        {
            task.Refresh();
        }
    }

    public void Dispose()
    {
        AppCultureManager.CultureChanged -= OnCultureChanged;
        _scheduler.TaskStatusChanged -= OnTaskStatusChanged;
    }
}
