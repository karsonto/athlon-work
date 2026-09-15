using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Threading;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Plan;
using Athlon.Agent.Infrastructure.Prompt;

namespace Athlon.Agent.Tests.Plan;

public sealed class ApprovedPlanRuntimeContributorTests
{
    [Fact]
    public void Append_InjectsPlanSummaryAndFilePath_InAgentMode()
    {
        var root = CreateTempRoot();
        var artifactStore = CreateArtifactStore(root);
        SyncOverAsync.Run(() => artifactStore.SaveAsync("session-1", CompletePlan, BuildRun()));

        var builder = new StringBuilder();
        CreateSut(SessionAgentMode.Agent, artifactStore).Append(builder, CreateContext(SessionAgentMode.Agent, "session-1"));

        var text = builder.ToString();
        Assert.Contains("## Approved Session Plan", text, StringComparison.Ordinal);
        Assert.Contains("Fix auth token refresh", text, StringComparison.Ordinal);
        Assert.Contains("impl", text, StringComparison.Ordinal);
        Assert.Contains("Read the token store", text, StringComparison.Ordinal);
        Assert.Contains("Acceptance criteria:", text, StringComparison.Ordinal);
        Assert.Contains("plan.md", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_Skips_InPlanMode()
    {
        var root = CreateTempRoot();
        var artifactStore = CreateArtifactStore(root);
        SyncOverAsync.Run(() => artifactStore.SaveAsync("session-1", CompletePlan, BuildRun()));

        var builder = new StringBuilder();
        CreateSut(SessionAgentMode.Plan, artifactStore).Append(builder, CreateContext(SessionAgentMode.Plan, "session-1"));

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_Skips_WhenNoApprovedPlanExists()
    {
        var root = CreateTempRoot();
        var artifactStore = CreateArtifactStore(root);

        var builder = new StringBuilder();
        CreateSut(SessionAgentMode.Agent, artifactStore).Append(builder, CreateContext(SessionAgentMode.Agent, "never-built"));

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_Skips_WhenWorkspaceMissing()
    {
        var root = CreateTempRoot();
        var artifactStore = CreateArtifactStore(root);
        SyncOverAsync.Run(() => artifactStore.SaveAsync("session-1", CompletePlan, BuildRun()));

        var seeded = CreateContext(SessionAgentMode.Agent, "session-1");
        var builder = new StringBuilder();
        CreateSut(SessionAgentMode.Agent, artifactStore).Append(builder, new EnvironmentPromptContext
        {
            Session = seeded.Session,
            WorkspaceRoot = null,
            Tools = [],
            SkillsDirectory = seeded.SkillsDirectory,
            Host = seeded.Host,
            PromptSettings = new PromptSettings(),
            AgentMode = SessionAgentMode.Agent
        });

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_InlinesPlanBody_WhenSshWorkspaceCannotReadTheFile()
    {
        var root = CreateTempRoot();
        var artifactStore = CreateArtifactStore(root);
        SyncOverAsync.Run(() => artifactStore.SaveAsync("session-1", CompletePlan, BuildRun()));

        var builder = new StringBuilder();
        CreateSut(SessionAgentMode.Agent, artifactStore).Append(builder, CreateContext(
            SessionAgentMode.Agent,
            "session-1",
            WorkspaceKind.Ssh));

        var text = builder.ToString();
        Assert.Contains("Full plan document:", text, StringComparison.Ordinal);
        // The unreadable local path must not be advertised as a file the model can open.
        Assert.DoesNotContain("Full plan file:", text, StringComparison.Ordinal);
        Assert.Contains("Cover with tests", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractSection_CollectsCheckboxAndBulletItems_IgnoringOtherSections()
    {
        var items = ApprovedPlanRuntimeContributor.ExtractSection(CompletePlan, "Acceptance");

        Assert.Equal(
            ["Tokens refresh before expiry", "Tests pass"],
            items);
    }

    private const string CompletePlan = """
        # Fix auth token refresh

        Refresh OAuth tokens before expiry so sessions do not drop mid-request.

        ## Steps
        1. Read the token store
        2. Add a refresh timer
        3. Cover with tests

        ## Acceptance
        - [ ] Tokens refresh before expiry
        - [ ] Tests pass
        """;

    private static PlanRun BuildRun() => new()
    {
        Id = "run-1",
        SessionId = "session-1",
        Goal = "Add token refresh",
        Title = "Fix auth token refresh",
        Todos =
        [
            new PlanTodoItem { Id = "impl", Content = "Read the token store" },
            new PlanTodoItem { Id = "timer", Content = "Add a refresh timer" }
        ]
    };

    private static ApprovedPlanRuntimeContributor CreateSut(
        SessionAgentMode mode,
        IPlanArtifactStore artifactStore) =>
        new(artifactStore, new StubHarnessState(mode), new AgentRunContextAccessor());

    private static IPlanArtifactStore CreateArtifactStore(string root)
    {
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        return new FilePlanArtifactStore(
            paths,
            new JsonFileStore(),
            new AgentRunContextAccessor());
    }

    private static EnvironmentPromptContext CreateContext(
        SessionAgentMode mode,
        string sessionId,
        WorkspaceKind workspaceKind = WorkspaceKind.Local) =>
        new()
        {
            // AgentSession.Create's argument is the *title*; the id must be set explicitly so the
            // artifact store is queried for the same session the fixture saved.
            Session = AgentSession.Create(sessionId) with { Id = sessionId },
            WorkspaceRoot = @"C:\work\demo",
            WorkspaceName = "demo",
            WorkspaceKind = workspaceKind,
            IgnorePatterns = [".git"],
            Tools =
            [
                new ToolDefinition("file_read", "Read", ToolSchema.Object().Build()),
                new ToolDefinition("todo_write", "Todos", ToolSchema.Object().Build())
            ],
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(
                @"C:\Users\test\.athlon-agent\skills",
                @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings(),
            AgentMode = mode
        };

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "athlon-approved-plan-" + Guid.NewGuid().ToString("N"));

    /// <summary>Minimal <see cref="ISessionHarnessState"/>; only <c>GetMode</c> matters here.</summary>
    private sealed class StubHarnessState(SessionAgentMode mode) : ISessionHarnessState
    {
        public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public SessionHarnessSnapshot GetSnapshot(string? sessionId) => new(mode);
        public SessionAgentMode GetMode(string? sessionId) => mode;
        public bool IsCodingMode(string? sessionId) => mode == SessionAgentMode.Coding;
        public bool IsAskMode(string? sessionId) => mode == SessionAgentMode.Ask;
        public bool IsPlanMode(string? sessionId) => mode == SessionAgentMode.Plan;
        public bool IsDebugMode(string? sessionId) => mode == SessionAgentMode.Debug;
        public bool IsEnabled(string? sessionId) => true;
        public bool IsCodingModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => mode == SessionAgentMode.Coding;
        public bool IsAskModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => mode == SessionAgentMode.Ask;
        public bool IsPlanModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => mode == SessionAgentMode.Plan;
        public bool IsDebugModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => mode == SessionAgentMode.Debug;
        public bool IsEnabledForActiveRun(IAgentRunContextAccessor runContextAccessor) => true;
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
