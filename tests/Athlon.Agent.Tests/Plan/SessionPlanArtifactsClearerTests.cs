using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Harness;
using Athlon.Agent.Infrastructure.Plan;

namespace Athlon.Agent.Tests.Plan;

/// <summary>
/// Clearing a plan used to be split across callers, each doing a different subset: clearing the
/// context wiped tasks but left the plan, while deleting a session cleared the in-memory run but
/// left tasks on disk. These tests pin the unified behavior.
/// </summary>
public sealed class SessionPlanArtifactsClearerTests
{
    [Fact]
    public async Task ClearAsync_DropsTasksInMemoryPlanPhaseAndDiskArtifacts()
    {
        var root = CreateTempRoot();
        var fixture = await CreateFixtureAsync(root, "session-1");

        await fixture.Clearer.ClearAsync("session-1");

        Assert.Empty((await fixture.TaskListStore.GetAsync("session-1")).Items);
        Assert.Null(fixture.PhaseAccessor.GetPhase("session-1"));
        Assert.Null(fixture.PhaseAccessor.GetActiveRun("session-1"));
        Assert.Null(await fixture.RunStore.LoadActiveAsync("session-1"));
        Assert.Null(await fixture.ArtifactStore.LoadAsync("session-1"));

        var sessionDir = Path.Combine(root, "sessions", "session-1");
        Assert.False(File.Exists(Path.Combine(sessionDir, FilePlanArtifactStore.MarkdownFileName)));
        Assert.False(File.Exists(Path.Combine(sessionDir, FilePlanArtifactStore.SnapshotFileName)));
    }

    [Fact]
    public async Task ClearAsync_ResetsAutoContinueBudget_SoThePlanDoesNotResume()
    {
        var root = CreateTempRoot();
        var fixture = await CreateFixtureAsync(root, "session-1");
        fixture.Tracker.Increment("session-1");
        fixture.Tracker.Stop("session-1");

        await fixture.Clearer.ClearAsync("session-1");

        Assert.Equal(0, fixture.Tracker.GetCount("session-1"));
        Assert.False(fixture.Tracker.IsStopped("session-1"));
    }

    [Fact]
    public async Task ClearAsync_CanKeepTheAutoContinueBudget_WhenOnlyTasksAreReset()
    {
        var root = CreateTempRoot();
        var fixture = await CreateFixtureAsync(root, "session-1");
        fixture.Tracker.Increment("session-1");

        await fixture.Clearer.ClearAsync("session-1", resetAutoContinue: false);

        Assert.Equal(1, fixture.Tracker.GetCount("session-1"));
    }

    [Fact]
    public async Task ClearAsync_NotifiesTaskListChanged_AndClearsTheTimelineCard()
    {
        var root = CreateTempRoot();
        var fixture = await CreateFixtureAsync(root, "session-1");

        await fixture.Clearer.ClearAsync("session-1");

        Assert.Equal("session-1", fixture.Notifier.LastNotifiedSessionId);
        // Displayed session matches, so the timeline plan card is dropped.
        Assert.Equal(1, fixture.TimelineClearCount);
    }

    [Fact]
    public async Task ClearAsync_IsIdempotent_AndIgnoresBlankSessionIds()
    {
        var root = CreateTempRoot();
        var fixture = await CreateFixtureAsync(root, "session-1");

        await fixture.Clearer.ClearAsync("session-1");
        await fixture.Clearer.ClearAsync("session-1");
        await fixture.Clearer.ClearAsync("");

        Assert.Empty((await fixture.TaskListStore.GetAsync("session-1")).Items);
    }

    private const string CompletePlan = """
        # Fix auth token refresh

        Refresh OAuth tokens before expiry.

        ## Steps
        1. Read the token store

        ## Acceptance
        - [ ] Tokens refresh before expiry
        """;

    private static async Task<Fixture> CreateFixtureAsync(string root, string sessionId)
    {
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        var runContextAccessor = new AgentRunContextAccessor();
        var jsonStore = new JsonFileStore();
        var runStore = new InMemoryPlanRunStore();
        var artifactStore = new FilePlanArtifactStore(paths, jsonStore, runContextAccessor);
        var taskListStore = new FileSessionTaskListStore(paths, jsonStore, runContextAccessor);
        var phaseAccessor = new PlanPhaseAccessor();
        var tracker = new PlanContinuationTracker();
        var notifier = new RecordingTaskListChangedNotifier();

        await runStore.WritePlanMarkdownAsync(sessionId, CompletePlan);
        var run = new PlanRun
        {
            Id = "run-1",
            SessionId = sessionId,
            Phase = PlanPhase.Done,
            Status = PlanRunStatuses.Approved,
            PlanMarkdown = CompletePlan
        };
        await runStore.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);
        await artifactStore.SaveAsync(sessionId, CompletePlan, run);
        await taskListStore.ReplaceAsync(sessionId, new SessionTaskList
        {
            Items = [new AgentTaskItem { Id = "impl", Content = "Read the token store", Status = AgentTaskStatuses.Completed }]
        });

        var planBar = new PlanActionBarViewModel(
            sessionTurns: null!,
            planOrchestrator: null!,
            planSessionState: new PlanSessionState(),
            planPhaseAccessor: phaseAccessor,
            planRunStore: runStore,
            localization: new LocalizationService());

        var timelineClearCount = 0;
        planBar.Configure(
            getDisplayedSessionId: () => sessionId,
            getSession: () => AgentSession.Create(sessionId),
            setSession: _ => { },
            showToast: (_, _) => { },
            onBuildApprovedAsync: () => Task.CompletedTask,
            onPlanTimelineCleared: () => timelineClearCount++);

        var clearer = new SessionPlanArtifactsClearer(
            phaseAccessor,
            runStore,
            artifactStore,
            taskListStore,
            notifier,
            tracker,
            planBar,
            new NoOpAppLogger());

        return new Fixture(
            clearer,
            taskListStore,
            artifactStore,
            runStore,
            phaseAccessor,
            tracker,
            notifier,
            () => timelineClearCount);
    }

    private sealed record Fixture(
        ISessionPlanArtifactsClearer Clearer,
        ISessionTaskListStore TaskListStore,
        IPlanArtifactStore ArtifactStore,
        IPlanRunStore RunStore,
        PlanPhaseAccessor PhaseAccessor,
        IPlanContinuationTracker Tracker,
        RecordingTaskListChangedNotifier Notifier,
        Func<int> TimelineClearCountAccessor)
    {
        public int TimelineClearCount => TimelineClearCountAccessor();
    }

    private sealed class RecordingTaskListChangedNotifier : ITaskListChangedNotifier
    {
        public event Action<string>? TaskListChanged;

        public string? LastNotifiedSessionId { get; private set; }

        public void Notify(string sessionId)
        {
            LastNotifiedSessionId = sessionId;
            TaskListChanged?.Invoke(sessionId);
        }
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "athlon-plan-clearer-" + Guid.NewGuid().ToString("N"));

    private sealed class NoOpAppLogger : IAppLogger
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

        public string ResolveSkillPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
