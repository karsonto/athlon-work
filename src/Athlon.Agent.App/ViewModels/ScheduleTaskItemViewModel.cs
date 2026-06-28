using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ScheduleTaskItemViewModel : ObservableObject
{
    private readonly ScheduledTask _task;
    private readonly AppSettings _settings;
    private readonly IFileStorageService _storage;
    private readonly SchedulerService _scheduler;
    private readonly ILocalizationService _loc;
    private readonly IUserNotifier _notifier;
    private readonly Action<ScheduleTaskItemViewModel>? _onDeleted;
    private readonly Func<string, Task>? _openSession;

    public ScheduleTaskItemViewModel(
        ScheduledTask task,
        AppSettings settings,
        IFileStorageService storage,
        SchedulerService scheduler,
        ILocalizationService localization,
        IUserNotifier notifier,
        Action<ScheduleTaskItemViewModel>? onDeleted = null,
        Func<string, Task>? openSession = null)
    {
        _task = task;
        _settings = settings;
        _storage = storage;
        _scheduler = scheduler;
        _loc = localization;
        _notifier = notifier;
        _onDeleted = onDeleted;
        _openSession = openSession;
        Refresh();
    }

    public string Id => _task.Id;

    [ObservableProperty]
    private string title = "";

    [ObservableProperty]
    private string promptPreview = "";

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    private string statusIcon = "⏳";

    [ObservableProperty]
    private string statusDisplay = "";

    [ObservableProperty]
    private string scheduleDescription = "";

    [ObservableProperty]
    private string nextRunDisplay = "";

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isVisible = true;

    [ObservableProperty]
    private string lastMessage = "";

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private bool isResultExpanded;

    [ObservableProperty]
    private bool canOpenSession;

    [ObservableProperty]
    private string lastRunStartDisplay = "";

    [ObservableProperty]
    private string lastRunEndDisplay = "";

    [ObservableProperty]
    private bool hasLastRunTiming;

    public string LastStatus => _task.LastStatus;

    public ScheduledTask Task => _task;

    partial void OnEnabledChanged(bool value)
    {
        _task.Enabled = value;
        _ = PersistAsync();
    }

    public void Refresh()
    {
        Title = _task.Title;
        PromptPreview = string.IsNullOrWhiteSpace(_task.Prompt)
            ? _loc["Schedule_NoPrompt"]
            : (_task.Prompt.Length > 120 ? _task.Prompt[..120] + "..." : _task.Prompt);
        Enabled = _task.Enabled;
        IsRunning = _task.LastStatus == "running";
        LastMessage = _task.LastMessage ?? "";
        HasResult = !string.IsNullOrWhiteSpace(LastMessage);
        CanOpenSession = !string.IsNullOrWhiteSpace(_task.LastThreadId);
        LastRunStartDisplay = FormatRunDateTime(_task.LastRunAt);
        LastRunEndDisplay = string.IsNullOrWhiteSpace(_task.LastRunEndedAt)
            ? (string.IsNullOrWhiteSpace(_task.LastRunAt) ? "" : "-")
            : FormatRunDateTime(_task.LastRunEndedAt);
        HasLastRunTiming = !string.IsNullOrWhiteSpace(LastRunStartDisplay);

        ScheduleDescription = _task.Kind switch
        {
            "daily" => _loc.Format("Schedule_DescriptionDaily", _task.TimeOfDay),
            "at" => _loc.Format("Schedule_DescriptionAt", FormatAtTime(_task.AtTime)),
            "interval" => _loc.Format("Schedule_DescriptionInterval", _task.EveryMinutes),
            "manual" => _loc["Schedule_DescriptionManual"],
            _ => _task.Kind
        };

        NextRunDisplay = string.IsNullOrWhiteSpace(_task.NextRunAt)
            ? "-"
            : FormatDateTime(_task.NextRunAt);

        (StatusIcon, StatusDisplay) = _task.LastStatus switch
        {
            "running" => ("▶️", _loc["Schedule_StatusRunning"]),
            "success" => ("✅", _loc["Schedule_StatusSuccess"]),
            "error" => ("❌", _loc["Schedule_StatusError"]),
            _ => ("⏳", _loc["Schedule_StatusReady"])
        };
    }

    [RelayCommand]
    private async Task Run()
    {
        try
        {
            await _scheduler.RunNowAsync(_task);
            Refresh();
        }
        catch (Exception ex)
        {
            _task.LastStatus = "error";
            _task.LastMessage = ex.Message;
            Refresh();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _scheduler.CancelTask(_task.Id);
    }

    [RelayCommand]
    private async Task OpenSession()
    {
        if (_openSession is null || string.IsNullOrWhiteSpace(_task.LastThreadId))
        {
            return;
        }

        await _openSession(_task.LastThreadId);
    }

    [RelayCommand]
    private void Edit()
    {
        var window = new ScheduleTaskEditWindow(_task, _notifier, _loc);
        window.Owner = System.Windows.Application.Current.MainWindow;
        if (window.ShowDialog() == true)
        {
            Refresh();
            _ = PersistAsync();
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (!_notifier.ConfirmYesNo("Schedule_DeleteTitle", "Schedule_DeleteMessage", _task.Title))
        {
            return;
        }

        _settings.Schedule.Tasks.Remove(_task);
        _ = PersistAsync();
        _onDeleted?.Invoke(this);
    }

    [RelayCommand]
    private void ToggleResult()
    {
        IsResultExpanded = !IsResultExpanded;
    }

    private async Task PersistAsync()
    {
        try
        {
            await _storage.SaveSettingsAsync(_settings);
        }
        catch { }
    }

    private static string FormatAtTime(string atTime)
    {
        if (string.IsNullOrWhiteSpace(atTime))
        {
            return "-";
        }

        return FormatDateTime(atTime);
    }

    private static string FormatDateTime(string iso)
    {
        if (DateTime.TryParse(iso, out var dt))
        {
            return dt.ToLocalTime().ToString("MM-dd HH:mm");
        }

        return iso;
    }

    private static string FormatRunDateTime(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return "";
        }

        if (DateTime.TryParse(iso, out var dt))
        {
            return dt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        }

        return iso;
    }
}
