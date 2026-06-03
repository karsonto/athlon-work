using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Plan;

namespace Athlon.Agent.Tests;

public sealed class PlanToolTests
{
    [Fact]
    public async Task CreatePlan_And_GetPlan_RoundTrip()
    {
        var sessionId = "plan-tool-session";
        var sessionContext = new ActiveAgentSessionContext();
        using var sessionScope = sessionContext.Enter(sessionId);
        var router = CreateRouter(sessionContext: sessionContext).Router;

        var create = await router.InvokeAsync(new ToolInvocation(
            "create_plan",
            new Dictionary<string, string>
            {
                ["name"] = "Ship feature",
                ["description"] = PlanTestFixtures.ShortSummary,
                ["expected_outcome"] = PlanTestFixtures.PlanOutcome,
                ["overview"] = PlanTestFixtures.Overview(),
                ["architecture"] = "Core holds models; Infrastructure holds tools.",
                ["subtasks"] =
                    """
                    [{"name":"Design","description":"Define AgentPlan fields and PlanMarkdownFormatter sections for Cursor-style output.","expected_outcome":"plan.md shows Overview, Architecture, and Implementation Plan.","files":["src/Athlon.Agent.Core/Plan/AgentPlan.cs"]},{"name":"Build","description":"Wire CreatePlanTool parameters and validation in PlanNotebook.","expected_outcome":"create_plan rejects shallow subtasks.","files":["src/Athlon.Agent.Infrastructure/Plan/CreatePlanTool.cs"]}]
                    """
            }));

        Assert.True(create.Succeeded);

        var get = await router.InvokeAsync(new ToolInvocation("get_plan", new Dictionary<string, string>()));
        Assert.True(get.Succeeded);
        Assert.Contains("Ship feature", get.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("## Overview", get.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("[WIP]", get.Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinishSubtask_UpdatesPlanFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-plan-tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sessionId = "finish-tool-session";
            using var ws = SessionWorkspaceScope.Enter(root);
            var sessionContext = new ActiveAgentSessionContext();
            using var sessionScope = sessionContext.Enter(sessionId);

            var settings = new AppSettings
            {
                Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = root } }
            };
            var (router, notebook) = CreateRouter(settings, root, sessionContext);

            await router.InvokeAsync(new ToolInvocation(
                "create_plan",
                new Dictionary<string, string>
                {
                    ["name"] = "Two step",
                    ["description"] = PlanTestFixtures.ShortSummary,
                    ["expected_outcome"] = PlanTestFixtures.PlanOutcome,
                    ["overview"] = PlanTestFixtures.Overview(),
                    ["subtasks"] =
                        """
                        [{"name":"One","description":"First implementation step with concrete file updates and tests.","expected_outcome":"Step one verified in plan.md and tests.","files":["src/a.cs"]},{"name":"Two","description":"Second implementation step completing integration and docs.","expected_outcome":"Step two verified end-to-end.","files":["src/b.cs"]}]
                        """
                }));

            notebook.ApprovePlan(sessionId);

            var finish = await router.InvokeAsync(new ToolInvocation(
                "finish_subtask",
                new Dictionary<string, string>
                {
                    ["subtask_idx"] = "0",
                    ["subtask_outcome"] = "Step one complete"
                }));

            Assert.True(finish.Succeeded);
            var planMd = await File.ReadAllTextAsync(Path.Combine(root, "plan.md"));
            Assert.Contains("- [x] One", planMd, StringComparison.Ordinal);
            Assert.Contains("[WIP]", planMd, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static (ToolRouter Router, PlanNotebook Notebook) CreateRouter(
        AppSettings? settings = null,
        string? workspaceRoot = null,
        ActiveAgentSessionContext? sessionContext = null)
    {
        settings ??= new AppSettings();
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            settings.Workspaces.Add(new WorkspaceSettings { Name = "test", RootPath = workspaceRoot });
        }

        var workspaceContext = new ActiveWorkspaceContext();
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            workspaceContext.SetWorkspace(workspaceRoot, "test");
        }

        var paths = new TestPathProvider(Path.GetTempPath());
        var guard = new WorkspaceGuard(workspaceContext, settings, paths);
        var notebook = new PlanNotebook(settings, guard);
        sessionContext ??= new ActiveAgentSessionContext();

        IAgentTool[] tools =
        [
            new CreatePlanTool(notebook, sessionContext),
            new FinishSubtaskTool(notebook, sessionContext),
            new GetPlanTool(notebook, sessionContext)
        ];

        return (new ToolRouter(tools), notebook);
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
