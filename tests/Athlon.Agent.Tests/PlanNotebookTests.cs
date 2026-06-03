using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Plan;

namespace Athlon.Agent.Tests;

public sealed class PlanNotebookTests
{
    [Fact]
    public void CreatePlan_KeepsSubtasksTodoAndDraftPhase()
    {
        var notebook = CreateNotebook();
        var result = notebook.CreatePlan("session-1", PlanTestFixtures.SampleRequest("Fix bug", 2));

        Assert.True(result.Success);
        var plan = notebook.GetCurrent("session-1");
        Assert.NotNull(plan);
        Assert.Equal(PlanPhase.Draft, plan.Phase);
        Assert.All(plan.Subtasks, subtask => Assert.Equal(SubTaskState.Todo, subtask.State));
    }

    [Fact]
    public void CreatePlan_RejectsShortOverview()
    {
        var notebook = CreateNotebook();
        var result = notebook.CreatePlan(
            "s",
            PlanTestFixtures.SampleRequest(overview: "too short"));

        Assert.False(result.Success);
        Assert.Contains("overview", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreatePlan_RejectsShortSubtaskDescription()
    {
        var notebook = CreateNotebook();
        var request = new CreatePlanRequest(
            "P",
            PlanTestFixtures.ShortSummary,
            PlanTestFixtures.PlanOutcome,
            PlanTestFixtures.Overview(),
            [new SubTaskInput("A", "short", "measurable acceptance criteria here ok")]);

        var result = notebook.CreatePlan("s", request);
        Assert.False(result.Success);
        Assert.Contains("description", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovePlan_ActivatesFirstSubtask()
    {
        var notebook = CreateNotebook();
        notebook.CreatePlan("session-1", PlanTestFixtures.SampleRequest());

        var approve = notebook.ApprovePlan("session-1");
        Assert.True(approve.Success);

        var plan = notebook.GetCurrent("session-1");
        Assert.Equal(PlanPhase.Approved, plan!.Phase);
        Assert.Equal(SubTaskState.InProgress, plan.Subtasks[0].State);
        Assert.Equal(SubTaskState.Todo, plan.Subtasks[1].State);
    }

    [Fact]
    public void FinishSubtask_RequiresApprovedPlan()
    {
        var notebook = CreateNotebook();
        notebook.CreatePlan("session-1", PlanTestFixtures.SampleRequest());

        var fail = notebook.FinishSubtask("session-1", 0, "done early");
        Assert.False(fail.Success);
        Assert.Contains("approved", fail.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinishSubtask_RequiresPreviousDone()
    {
        var notebook = CreateNotebook();
        notebook.CreatePlan("session-1", PlanTestFixtures.SampleRequest());
        notebook.ApprovePlan("session-1");

        var fail = notebook.FinishSubtask("session-1", 1, "done early");
        Assert.False(fail.Success);
        Assert.Contains("previous subtask", fail.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinishSubtask_ActivatesNextSubtask()
    {
        var notebook = CreateNotebook();
        notebook.CreatePlan("session-1", PlanTestFixtures.SampleRequest());
        notebook.ApprovePlan("session-1");

        var ok = notebook.FinishSubtask("session-1", 0, "A completed");
        Assert.True(ok.Success);

        var plan = notebook.GetCurrent("session-1");
        Assert.Equal(SubTaskState.Done, plan!.Subtasks[0].State);
        Assert.Equal(SubTaskState.InProgress, plan.Subtasks[1].State);
        Assert.Equal(PlanPhase.Approved, plan.Phase);
    }

    [Fact]
    public void CreatePlan_RejectsTooManySubtasks()
    {
        var settings = new AppSettings { Plan = new PlanSettings { MaxSubtasks = 2 } };
        var notebook = new PlanNotebook(settings, CreateWorkspaceGuard(settings));
        var request = PlanTestFixtures.SampleRequest(subtaskCount: 3);

        var result = notebook.CreatePlan("s", request);
        Assert.False(result.Success);
        Assert.Contains("maximum", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPlanMarkdown_WithoutPlan_ReturnsHint()
    {
        var notebook = CreateNotebook();
        Assert.Contains("create_plan", notebook.GetPlanMarkdown("missing"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPlanMarkdown_IncludesSectionsAndFiles()
    {
        var notebook = CreateNotebook();
        var request = new CreatePlanRequest(
            "Rich plan",
            PlanTestFixtures.ShortSummary,
            PlanTestFixtures.PlanOutcome,
            PlanTestFixtures.Overview(),
            [PlanTestFixtures.Subtask("Wire schema", "details", "src/A.cs", "src/B.cs")],
            Architecture: "Layered: Core, Infrastructure, App.",
            Mermaid: "stateDiagram-v2\n  [*] --> Draft\n  Draft --> Approved",
            TestingStrategy: "Run dotnet test.",
            OutOfScope: "Packaging changes.");
        notebook.CreatePlan("s", request);

        var markdown = notebook.GetPlanMarkdown("s");
        Assert.Contains("## Overview", markdown, StringComparison.Ordinal);
        Assert.Contains("## Architecture", markdown, StringComparison.Ordinal);
        Assert.Contains("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Contains("## Testing Strategy", markdown, StringComparison.Ordinal);
        Assert.Contains("**Files:**", markdown, StringComparison.Ordinal);
        Assert.Contains("`src/A.cs`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_RemovesMemoryPlanAndDeletesPlanMd()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-plan-clear", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var _ = SessionWorkspaceScope.Enter(root);
            var settings = new AppSettings
            {
                Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = root } }
            };
            var notebook = new PlanNotebook(settings, CreateWorkspaceGuard(settings));
            notebook.CreatePlan("s1", PlanTestFixtures.SampleRequest("Demo", 1));

            var planPath = Path.Combine(root, "plan.md");
            Assert.True(File.Exists(planPath));

            notebook.Clear("s1");

            Assert.Null(notebook.GetCurrent("s1"));
            Assert.False(File.Exists(planPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void CreatePlan_SyncsPlanMd_WhenWorkspaceConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-plan-ws", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var _ = SessionWorkspaceScope.Enter(root);
            var settings = new AppSettings
            {
                Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = root } }
            };
            var notebook = new PlanNotebook(settings, CreateWorkspaceGuard(settings));
            notebook.CreatePlan("s1", PlanTestFixtures.SampleRequest("Demo", 1));

            var planPath = Path.Combine(root, "plan.md");
            Assert.True(File.Exists(planPath));
            var content = File.ReadAllText(planPath);
            Assert.Contains("# Plan: Demo", content, StringComparison.Ordinal);
            Assert.Contains("## Overview", content, StringComparison.Ordinal);
            Assert.DoesNotContain("[WIP]", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryGetPlanFilePath_ReturnsNullWithoutWorkspace()
    {
        var notebook = CreateNotebook();
        Assert.Null(notebook.TryGetPlanFilePath());
    }

    [Fact]
    public void TryGetPlanFilePath_ReturnsPlanMdPathWhenWorkspaceConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-plan-path", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var _ = SessionWorkspaceScope.Enter(root);
            var settings = new AppSettings
            {
                Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = root } }
            };
            var notebook = new PlanNotebook(settings, CreateWorkspaceGuard(settings));

            var path = notebook.TryGetPlanFilePath();

            Assert.NotNull(path);
            Assert.Equal(Path.Combine(root, "plan.md"), path);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static PlanNotebook CreateNotebook() =>
        new(new AppSettings(), CreateWorkspaceGuard(new AppSettings()));

    private static WorkspaceGuard CreateWorkspaceGuard(AppSettings settings) =>
        new(new InMemoryWorkspaceContext(), settings, new TestPathProvider(Path.GetTempPath()));

    private sealed class InMemoryWorkspaceContext : IActiveWorkspaceContext
    {
        public string? RootPath { get; private set; }
        public string? DisplayName { get; private set; }
        public IReadOnlyList<string> IgnorePatterns { get; private set; } = [];

        public void SetWorkspace(string? rootPath, string? displayName = null, IReadOnlyList<string>? ignorePatterns = null)
        {
            RootPath = rootPath;
            DisplayName = displayName;
            IgnorePatterns = ignorePatterns ?? [];
        }
    }

    private sealed class TestPathProvider(string rootPath) : IAppPathProvider
    {
        public string RootPath { get; } = rootPath;
        public string ConfigPath => Path.Combine(rootPath, "config");
        public string SessionsPath => Path.Combine(rootPath, "sessions");
        public string AuditPath => Path.Combine(rootPath, "audit");
        public string LogsPath => Path.Combine(rootPath, "logs");
        public string CredentialsPath => Path.Combine(rootPath, "credentials");
        public string SkillsPath => Path.Combine(rootPath, "skills");

        public void EnsureCreated() { }

        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
