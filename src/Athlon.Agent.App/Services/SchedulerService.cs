using System.Collections.Concurrent;
using Athlon.Agent.Core;
namespace Athlon.Agent.App.Services;

public sealed class SchedulerService : IDisposable
{
    private readonly IAgentRuntime _runtime;
    private readonly IFileStorageService _storage;
    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTasks = new();

    private Timer? _timer;
    private bool _started;
    private const int PollIntervalMs = 15_000;

    public event EventHandler<ScheduledTaskStatusEventArgs>? TaskStatusChanged;

    public SchedulerService(
        IAgentRuntime runtime,
        IFileStorageService storage,
        AppSettings settings,
        IAppLogger logger)
    {
        _runtime = runtime;
        _storage = storage;
        _settings = settings;
        _logger = logger.ForContext("SchedulerService");
    }

    public bool IsRunning => _started;

    public IReadOnlyList<ScheduledTask> Tasks => _settings.Schedule.Tasks;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timer = new Timer(OnPoll, null, PollIntervalMs, PollIntervalMs);
        _logger.Information("Scheduler started");
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _timer?.Dispose();
        _timer = null;
        _logger.Information("Scheduler stopped");
    }

    public async Task RunNowAsync(ScheduledTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _logger.Information("Manual run requested: {Title} ({Id})", task.Title, task.Id);
        await ExecuteTaskAsync(task);
    }

    public void CancelTask(string taskId)
    {
        if (_runningTasks.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
        }
    }

    private void OnPoll(object? state)
    {
        if (!_started || !_settings.Schedule.Enabled)
        {
            return;
        }

        foreach (var task in _settings.Schedule.Tasks)
        {
            if (!task.Enabled || task.Kind == "manual")
            {
                continue;
            }

            if (_runningTasks.ContainsKey(task.Id))
            {
                continue;
            }

            if (!ScheduleTiming.IsDue(task))
            {
                continue;
            }

            _ = ExecuteTaskAsync(task);
        }
    }

    private async Task ExecuteTaskAsync(ScheduledTask task)
    {
        var cts = new CancellationTokenSource();
        if (!_runningTasks.TryAdd(task.Id, cts))
        {
            cts.Dispose();
            _logger.Warning("Task already running: {Title}", task.Title);
            return;
        }

        SystemKeepAwakeHelper.Acquire();
        try
        {
            _logger.Information("Executing task: {Title}", task.Title);
            NotifyStatus(task, "running", "");

            task.LastStatus = "running";
            task.LastRunAt = DateTime.UtcNow.ToString("O");
            await PersistSettingsAsync();

            var workspaceRoot = ScheduleTiming.ResolveWorkspaceRoot(task);
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                task.LastStatus = "error";
                task.LastMessage = "未配置工作目录";
                NotifyStatus(task, "error", task.LastMessage);
                return;
            }

            if (_settings.Schedule.RequireToolApproval)
            {
                task.LastStatus = "error";
                task.LastMessage = "定时任务已启用工具审批，但无人值守无法确认";
                NotifyStatus(task, "error", task.LastMessage);
                return;
            }

            var session = AgentSession.Create($"定时任务: {task.Title}")
                .WithWorkspace(workspaceRoot);

            var result = await _runtime.SendAsync(
                session,
                task.Prompt,
                callbacks: new AgentTurnCallbacks
                {
                    OnSessionUpdated = _ => Task.CompletedTask
                },
                options: new AgentSendOptions { RequireToolApproval = false },
                cancellationToken: cts.Token);

            await _storage.SaveSessionAsync(result);

            var lastMessage = result.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
            var resultText = lastMessage?.Content ?? "(无返回)";
            var truncated = resultText.Length > 500 ? resultText[..500] + "…" : resultText;

            task.LastStatus = "success";
            task.LastMessage = truncated;
            task.LastThreadId = result.Id;

            NotifyStatus(task, "success", truncated);
            _logger.Information("Task completed: {Title}", task.Title);
        }
        catch (OperationCanceledException)
        {
            task.LastStatus = "idle";
            task.LastMessage = "已取消";
            NotifyStatus(task, "idle", "已取消");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Task failed: {Title}", task.Title);
            task.LastStatus = "error";
            task.LastMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            NotifyStatus(task, "error", task.LastMessage);
        }
        finally
        {
            if (_runningTasks.TryRemove(task.Id, out var removedCts))
            {
                removedCts.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(task.LastRunAt))
            {
                task.LastRunEndedAt = DateTime.UtcNow.ToString("O");
            }

            SystemKeepAwakeHelper.Release();
            task.NextRunAt = ScheduleTiming.ComputeNextRun(task);
            await PersistSettingsAsync();
        }
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            await _storage.SaveSettingsAsync(_settings);
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to persist settings: {Message}", ex.Message);
        }
    }

    private void NotifyStatus(ScheduledTask task, string status, string message)
    {
        TaskStatusChanged?.Invoke(this, new ScheduledTaskStatusEventArgs(task.Id, status, message));
    }

    public void Dispose()
    {
        Stop();
    }
}

public sealed class ScheduledTaskStatusEventArgs(string taskId, string status, string message) : EventArgs
{
    public string TaskId { get; } = taskId;
    public string Status { get; } = status;
    public string Message { get; } = message;
}
