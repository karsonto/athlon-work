using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Harness;

namespace Athlon.Agent.Tests;

public sealed class ComposerHarnessViewModelTests
{
    private static readonly ILocalizationService Localization = new LocalizationService();

    [Fact]
    public async Task ClearTaskPlanAsync_ClearsStoreAndSidebarTasks()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "task", Status = AgentTaskStatuses.InProgress }
        ]);
        var vm = new ComposerHarnessViewModel(harness, store, new NoOpTaskPlanCompletionNotifier(), Localization);

        await vm.LoadForSessionAsync("session-1");
        Assert.Single(vm.Tasks);
        Assert.True(vm.ShowTaskPanel);

        await vm.ClearTaskPlanAsync();

        Assert.Empty(vm.Tasks);
        Assert.False(vm.ShowTaskPanel);
        Assert.Equal(0, vm.PendingTaskCount);
        Assert.Equal(0, vm.InProgressTaskCount);
        var list = await store.GetAsync("session-1");
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task ShowTaskPanel_IsFalse_WhenHarnessDisabled()
    {
        var harness = new StubHarnessState(SessionAgentMode.Agent);
        var store = new MutableTaskListStore();
        var vm = new ComposerHarnessViewModel(harness, store, new NoOpTaskPlanCompletionNotifier(), Localization);

        await vm.LoadForSessionAsync("session-1");
        store.SetItems([new AgentTaskItem { Id = "1", Content = "task", Status = AgentTaskStatuses.Pending }]);
        await vm.RefreshTasksAsync();

        Assert.False(vm.ShowTaskPanel);
        Assert.Empty(vm.Tasks);
    }

    [Fact]
    public async Task ShowTaskPanel_IsFalse_WhenTaskListEmpty()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore();
        var vm = new ComposerHarnessViewModel(harness, store, new NoOpTaskPlanCompletionNotifier(), Localization);

        await vm.LoadForSessionAsync("session-1");

        Assert.False(vm.ShowTaskPanel);
        Assert.Empty(vm.Tasks);
    }

    [Fact]
    public async Task ShowTaskPanel_IsTrue_WhenHarnessEnabledAndTasksExist()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Pending }
        ]);
        var vm = new ComposerHarnessViewModel(harness, store, new NoOpTaskPlanCompletionNotifier(), Localization);

        await vm.LoadForSessionAsync("session-1");

        Assert.True(vm.ShowTaskPanel);
        Assert.Single(vm.Tasks);
        Assert.Equal("first", vm.Tasks[0].Content);
    }

    [Fact]
    public async Task RefreshTasksAsync_MergesById_UpdatesStatusAndAddsRemovesItems()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Pending },
            new AgentTaskItem { Id = "2", Content = "second", Status = AgentTaskStatuses.InProgress }
        ]);
        var vm = new ComposerHarnessViewModel(harness, store, new NoOpTaskPlanCompletionNotifier(), Localization);
        await vm.LoadForSessionAsync("session-1");

        store.SetItems(
        [
            new AgentTaskItem { Id = "1", Content = "first updated", Status = AgentTaskStatuses.Completed },
            new AgentTaskItem { Id = "3", Content = "third", Status = AgentTaskStatuses.Pending }
        ]);
        await vm.RefreshTasksAsync();

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("first updated", vm.Tasks.Single(task => task.Id == "1").Content);
        Assert.True(vm.Tasks.Single(task => task.Id == "1").IsCompleted);
        Assert.Equal("third", vm.Tasks.Single(task => task.Id == "3").Content);
        Assert.DoesNotContain(vm.Tasks, task => task.Id == "2");
    }

    [Fact]
    public async Task RefreshTasksAsync_TriggersCompletionAnimation_WhenStatusBecomesCompleted()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.InProgress }
        ]);
        var vm = new ComposerHarnessViewModel(harness, store, new NoOpTaskPlanCompletionNotifier(), Localization);
        await vm.LoadForSessionAsync("session-1");

        store.SetItems(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Completed }
        ]);
        await vm.RefreshTasksAsync();

        Assert.True(vm.Tasks[0].ShouldPlayCompletionAnimation);
    }

    [Fact]
    public async Task RefreshTasksAsync_DoesNotTriggerCompletionAnimation_ForInitiallyCompletedTask()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Completed }
        ]);
        var vm = new ComposerHarnessViewModel(harness, store, new NoOpTaskPlanCompletionNotifier(), Localization);

        await vm.LoadForSessionAsync("session-1");

        Assert.False(vm.Tasks[0].ShouldPlayCompletionAnimation);
    }

    [Fact]
    public async Task RefreshTasksAsync_NotifiesTaskCompleted_WhenLastTaskCompletesPlan()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Completed },
            new AgentTaskItem { Id = "2", Content = "second", Status = AgentTaskStatuses.InProgress }
        ]);
        var notifier = new RecordingTaskPlanCompletionNotifier();
        var vm = new ComposerHarnessViewModel(harness, store, notifier, Localization);
        await vm.LoadForSessionAsync("session-1");

        store.SetItems(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Completed },
            new AgentTaskItem { Id = "2", Content = "second", Status = AgentTaskStatuses.Completed }
        ]);
        await vm.RefreshTasksAsync();

        Assert.Equal(1, notifier.CallCount);
        Assert.Equal("second", notifier.LastCompletedTaskContent);
    }

    [Fact]
    public async Task RefreshTasksAsync_NotifiesTaskCompleted_WhenOnlyPartiallyComplete()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.InProgress },
            new AgentTaskItem { Id = "2", Content = "second", Status = AgentTaskStatuses.Pending }
        ]);
        var notifier = new RecordingTaskPlanCompletionNotifier();
        var vm = new ComposerHarnessViewModel(harness, store, notifier, Localization);
        await vm.LoadForSessionAsync("session-1");

        store.SetItems(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Completed },
            new AgentTaskItem { Id = "2", Content = "second", Status = AgentTaskStatuses.Pending }
        ]);
        await vm.RefreshTasksAsync();

        Assert.Equal(1, notifier.CallCount);
        Assert.Equal("first", notifier.LastCompletedTaskContent);
    }

    [Fact]
    public async Task LoadForSessionAsync_DoesNotNotifyTaskCompleted_WhenPlanAlreadyComplete()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Completed },
            new AgentTaskItem { Id = "2", Content = "second", Status = AgentTaskStatuses.Completed }
        ]);
        var notifier = new RecordingTaskPlanCompletionNotifier();
        var vm = new ComposerHarnessViewModel(harness, store, notifier, Localization);

        await vm.LoadForSessionAsync("session-1");

        Assert.Equal(0, notifier.CallCount);
    }

    [Fact]
    public async Task RefreshTasksAsync_DoesNotNotifyTaskCompleted_WhenAllCancelled()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.InProgress },
            new AgentTaskItem { Id = "2", Content = "second", Status = AgentTaskStatuses.Pending }
        ]);
        var notifier = new RecordingTaskPlanCompletionNotifier();
        var vm = new ComposerHarnessViewModel(harness, store, notifier, Localization);
        await vm.LoadForSessionAsync("session-1");

        store.SetItems(
        [
            new AgentTaskItem { Id = "1", Content = "first", Status = AgentTaskStatuses.Cancelled },
            new AgentTaskItem { Id = "2", Content = "second", Status = AgentTaskStatuses.Cancelled }
        ]);
        await vm.RefreshTasksAsync();

        Assert.Equal(0, notifier.CallCount);
    }

    [Fact]
    public async Task TodoWriteTool_NotifiesTaskListChanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-harness-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        var store = new FileSessionTaskListStore(paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessionContext = new ActiveAgentSessionContext();
        sessionContext.SetSession("session-1");
        var notifier = new TaskListChangedNotifier();
        string? notifiedSessionId = null;
        notifier.TaskListChanged += sessionId => notifiedSessionId = sessionId;
        var tool = new TodoWriteTool(store, sessionContext, notifier, new HarnessNoOpLogger());

        var result = await tool.InvokeAsync(new ToolInvocation("todo_write", new Dictionary<string, string>
        {
            ["todos"] = """[{"id":"1","content":"task","status":"pending"}]""",
            ["merge"] = "false"
        }));

        Assert.True(result.Succeeded);
        Assert.Equal("session-1", notifiedSessionId);
    }

    private sealed class NoOpTaskPlanCompletionNotifier : ITaskPlanCompletionNotifier
    {
        public void NotifyTaskCompleted(string completedTaskContent) { }
    }

    private sealed class RecordingTaskPlanCompletionNotifier : ITaskPlanCompletionNotifier
    {
        public int CallCount { get; private set; }

        public string? LastCompletedTaskContent { get; private set; }

        public void NotifyTaskCompleted(string completedTaskContent)
        {
            CallCount++;
            LastCompletedTaskContent = completedTaskContent;
        }
    }

    [Fact]
    public async Task SelectModeAsync_PersistsModeAndClearsTasksWhenLeavingCoding()
    {
        var harness = new StubHarnessState(SessionAgentMode.Coding);
        var store = new MutableTaskListStore(
        [
            new AgentTaskItem { Id = "1", Content = "task", Status = AgentTaskStatuses.Pending }
        ]);
        var vm = new ComposerHarnessViewModel(harness, store, new NoOpTaskPlanCompletionNotifier(), Localization);
        await vm.LoadForSessionAsync("session-1");

        Assert.Equal(SessionAgentMode.Coding, vm.SelectedMode);
        Assert.Single(vm.Tasks);

        await vm.SelectModeCommand.ExecuteAsync(SessionAgentMode.Ask);

        Assert.Equal(SessionAgentMode.Ask, vm.SelectedMode);
        Assert.Empty(vm.Tasks);
        Assert.False(vm.IsModePickerOpen);
    }

    private sealed class StubHarnessState(SessionAgentMode mode) : ISessionHarnessState
    {
        private readonly Dictionary<string, SessionAgentMode> _modeBySession = new(StringComparer.OrdinalIgnoreCase);

        public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _modeBySession[sessionId] = mode;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default)
        {
            _modeBySession[sessionId] = state.Mode;
            return Task.CompletedTask;
        }

        public SessionHarnessSnapshot GetSnapshot(string? sessionId) =>
            string.IsNullOrWhiteSpace(sessionId)
                ? SessionHarnessSnapshot.Empty
                : new SessionHarnessSnapshot(_modeBySession.GetValueOrDefault(sessionId, mode));

        public SessionAgentMode GetMode(string? sessionId) => GetSnapshot(sessionId).Mode;

        public bool IsCodingMode(string? sessionId) => GetMode(sessionId) == SessionAgentMode.Coding;

        public bool IsAskMode(string? sessionId) => GetMode(sessionId) == SessionAgentMode.Ask;

        public bool IsEnabled(string? sessionId) => IsCodingMode(sessionId);

        public bool IsCodingModeForActiveRun(IAgentRunContextAccessor runContextAccessor)
        {
            var run = runContextAccessor.Current;
            if (run is null || run.Kind == AgentRunKind.SubAgent)
            {
                return false;
            }

            return IsCodingMode(run.SessionId);
        }

        public bool IsAskModeForActiveRun(IAgentRunContextAccessor runContextAccessor)
        {
            var run = runContextAccessor.Current;
            if (run is null || run.Kind == AgentRunKind.SubAgent)
            {
                return false;
            }

            return IsAskMode(run.SessionId);
        }

        public bool IsEnabledForActiveRun(IAgentRunContextAccessor runContextAccessor) =>
            IsCodingModeForActiveRun(runContextAccessor);
    }

    private sealed class MutableTaskListStore : ISessionTaskListStore
    {
        private SessionTaskList _list;

        public MutableTaskListStore(IReadOnlyList<AgentTaskItem>? seed = null) =>
            _list = new SessionTaskList { Items = seed?.ToList() ?? [] };

        public void SetItems(IReadOnlyList<AgentTaskItem> items) =>
            _list = new SessionTaskList { Items = items.ToList() };

        public Task<SessionTaskList> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_list);

        public Task ReplaceAsync(string sessionId, SessionTaskList list, CancellationToken cancellationToken = default)
        {
            _list = list;
            return Task.CompletedTask;
        }

        public Task<SessionTaskList> ApplyMergeAsync(
            string sessionId,
            IReadOnlyList<AgentTaskItem> todos,
            bool merge,
            CancellationToken cancellationToken = default)
        {
            if (merge)
            {
                var byId = _list.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var todo in todos)
                {
                    byId[todo.Id] = todo;
                }

                _list = new SessionTaskList { Items = byId.Values.ToList() };
            }
            else
            {
                _list = new SessionTaskList { Items = todos.ToList() };
            }

            return Task.FromResult(_list);
        }
    }

    private sealed class HarnessNoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }

    private sealed class TestPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated() => Directory.CreateDirectory(SessionsPath);

        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
