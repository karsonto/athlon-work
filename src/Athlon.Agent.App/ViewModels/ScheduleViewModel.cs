using System.Collections.ObjectModel;
using System.Windows;
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
    private readonly Func<string, Task>? _openSession;
    private bool _isSyncing;

    public ScheduleViewModel(
        AppSettings settings,
        IFileStorageService storage,
        SchedulerService scheduler,
        Lazy<ISessionHost> sessionHost)
    {
        _settings = settings;
        _storage = storage;
        _scheduler = scheduler;
        _openSession = id => sessionHost.Value.OpenSessionByIdAsync(id);
        _scheduler.TaskStatusChanged += OnTaskStatusChanged;
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

    public ObservableCollection<ScheduleFilterChipViewModel> FilterChips { get; } = new()
    {
        new ScheduleFilterChipViewModel("全部", 0) { IsSelected = true },
        new ScheduleFilterChipViewModel("启用", 1),
        new ScheduleFilterChipViewModel("运行中", 2),
        new ScheduleFilterChipViewModel("已完成", 3)
    };

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
            ? $"共 {Tasks.Count} 个任务，{enabledCount} 个启用"
            : "暂无定时任务";
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
            Title = "新建定时任务",
            Kind = "daily",
            TimeOfDay = "09:00",
            Prompt = "",
            Mode = "agent",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };

        var window = new ScheduleTaskEditWindow(task, isNew: true);
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

    public void Dispose()
    {
        _scheduler.TaskStatusChanged -= OnTaskStatusChanged;
    }
}
