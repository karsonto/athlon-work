using Athlon.Agent.App.Services;
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
    private readonly Action<ScheduleTaskItemViewModel>? _onDeleted;
    private readonly Func<string, Task>? _openSession;

    public ScheduleTaskItemViewModel(
        ScheduledTask task,
        AppSettings settings,
        IFileStorageService storage,
        SchedulerService scheduler,
        Action<ScheduleTaskItemViewModel>? onDeleted = null,
        Func<string, Task>? openSession = null)
    {
        _task = task;
        _settings = settings;
        _storage = storage;
        _scheduler = scheduler;
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
    private string statusDisplay = "就绪";

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
            ? "(无 Prompt)"
            : (_task.Prompt.Length > 120 ? _task.Prompt[..120] + "..." : _task.Prompt);
        Enabled = _task.Enabled;
        IsRunning = _task.LastStatus == "running";
        LastMessage = _task.LastMessage ?? "";
        HasResult = !string.IsNullOrWhiteSpace(LastMessage);
        CanOpenSession = !string.IsNullOrWhiteSpace(_task.LastThreadId);

        ScheduleDescription = _task.Kind switch
        {
            "daily" => $"每天 {_task.TimeOfDay}",
            "at" => $"一次性 · {FormatAtTime(_task.AtTime)}",
            "interval" => $"每隔 {_task.EveryMinutes} 分钟",
            "manual" => "手动触发",
            _ => _task.Kind
        };

        NextRunDisplay = string.IsNullOrWhiteSpace(_task.NextRunAt)
            ? "-"
            : FormatDateTime(_task.NextRunAt);

        (StatusIcon, StatusDisplay) = _task.LastStatus switch
        {
            "running" => ("▶️", "运行中"),
            "success" => ("✅", "成功"),
            "error" => ("❌", "失败"),
            _ => ("⏳", "就绪")
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
        var window = new ScheduleTaskEditWindow(_task);
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
        var result = System.Windows.MessageBox.Show(
            $"确定要删除定时任务「{_task.Title}」吗？",
            "删除定时任务",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result != System.Windows.MessageBoxResult.Yes)
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
}
