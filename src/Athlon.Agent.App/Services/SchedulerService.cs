using System.Collections.Concurrent;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.App.Services;

public sealed class SchedulerService : IDisposable
{
    private readonly IAgentRuntime _runtime;
    private readonly IFileStorageService _storage;
    private readonly ISessionKnowledgeState _sessionKnowledgeState;
    private readonly IComputerUseDesktopCaptureSession _computerUseDesktop;
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
        ISessionKnowledgeState sessionKnowledgeState,
        IComputerUseDesktopCaptureSession computerUseDesktop,
        AppSettings settings,
        IAppLogger logger)
    {
        _runtime = runtime;
        _storage = storage;
        _sessionKnowledgeState = sessionKnowledgeState;
        _computerUseDesktop = computerUseDesktop;
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

        var keepAwake = _settings.Schedule.KeepAwake;
        IAsyncDisposable? desktopCapture = null;
        var acquiredKeepAwake = false;

        try
        {
            _logger.Information("Executing task: {Title}", task.Title);
            NotifyStatus(task, "running", "");

            task.LastStatus = "running";
            task.LastRunAt = DateTime.UtcNow.ToString("O");
            await PersistSettingsAsync();

            var schedule = _settings.Schedule;
            var workspaceRoot = ScheduleTiming.ResolveWorkspaceRoot(task, schedule);
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                task.LastStatus = "error";
                task.LastMessage = "未配置工作目录";
                NotifyStatus(task, "error", task.LastMessage);
                return;
            }

            var mode = ScheduleTiming.ResolveMode(task, schedule);
            var (allowToolCalls, maxRounds) = ScheduleTiming.ResolveModeOptions(mode);
            var computerUseActive = task.ComputerUse;
            if (computerUseActive)
            {
                // Computer Use requires tool calls regardless of ask/agent mode.
                allowToolCalls = true;
                // Minimize shell so BitBlt captures the desktop (same as interactive CU).
                desktopCapture = await _computerUseDesktop.BeginAsync(cts.Token).ConfigureAwait(false);
                // Keep the machine awake while capturing / interacting with the desktop.
                if (!keepAwake)
                {
                    SystemKeepAwakeHelper.Acquire();
                    acquiredKeepAwake = true;
                }
            }

            if (keepAwake && !acquiredKeepAwake)
            {
                SystemKeepAwakeHelper.Acquire();
                acquiredKeepAwake = true;
            }

            var modelOverride = ScheduleTiming.ResolveModelName(task, schedule, _settings.Model.ModelName);
            var prompt = ScheduleTiming.BuildPrompt(task, schedule);

            var disabledSkillNames = _settings.Skills
                .Where(s => !s.Enabled && !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => s.Name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<string>? skillAllowList;
            if (task.SkillNames is { Count: > 0 })
            {
                skillAllowList = task.SkillNames
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .Where(n => !disabledSkillNames.Contains(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            else
            {
                skillAllowList = null;
            }

            var globallyEnabledMcp = _settings.McpServers
                .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => s.Name);
            var mcpAllowList = ScheduleTiming.ResolveAllowList(task.McpServerNames, globallyEnabledMcp);

            var session = AgentSession.Create($"定时任务: {task.Title}")
                .WithWorkspace(workspaceRoot);

            var knowledgeIds = (task.KnowledgeModuleIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await _sessionKnowledgeState.SaveAsync(
                session.Id,
                new SessionKnowledgeSnapshot(
                    Enabled: knowledgeIds.Count > 0,
                    ModuleIds: knowledgeIds),
                cts.Token).ConfigureAwait(false);

            using var scope = ScheduleTurnScope.Enter(new ScheduleTurnOptions(
                ModelNameOverride: modelOverride,
                AllowToolCalls: allowToolCalls,
                MaxModelToolRounds: maxRounds,
                SkillNames: skillAllowList,
                McpServerNames: mcpAllowList));

            var loopOptions = maxRounds is null
                ? null
                : new AgentLoopOptions { MaxModelToolRounds = maxRounds };

            var result = await _runtime.SendAsync(
                session,
                prompt,
                callbacks: new AgentTurnCallbacks
                {
                    OnSessionUpdated = _ => Task.CompletedTask,
                    // Unattended schedule: auto-approve (e.g. computer_interact).
                    OnToolApprovalRequested = static (_, _) =>
                        Task.FromResult(ToolApprovalDecision.Approved)
                },
                cancellationToken: cts.Token,
                computerUseActive: computerUseActive,
                loopOptions: loopOptions);

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
            if (desktopCapture is not null)
            {
                await desktopCapture.DisposeAsync().ConfigureAwait(false);
            }

            if (_runningTasks.TryRemove(task.Id, out var removedCts))
            {
                removedCts.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(task.LastRunAt))
            {
                task.LastRunEndedAt = DateTime.UtcNow.ToString("O");
            }

            if (acquiredKeepAwake)
            {
                SystemKeepAwakeHelper.Release();
            }

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
