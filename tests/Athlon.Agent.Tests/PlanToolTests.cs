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
        var router = CreateRouter(sessionContext: sessionContext);

        var create = await router.InvokeAsync(new ToolInvocation(
            "create_plan",
            new Dictionary<string, string>
            {
                ["name"] = "Ship feature",
                ["description"] = "Implement MVP",
                ["expected_outcome"] = "Feature shipped",
                ["subtasks"] = """[{"name":"Design","description":"d","expected_outcome":"o"},{"name":"Build","description":"b","expected_outcome":"o2"}]"""
            }));

        Assert.True(create.Succeeded);

        var get = await router.InvokeAsync(new ToolInvocation("get_plan", new Dictionary<string, string>()));
        Assert.True(get.Succeeded);
        Assert.Contains("Ship feature", get.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[WIP]", get.Content ?? string.Empty, StringComparison.Ordinal);
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
            var router = CreateRouter(settings, root, sessionContext);

            await router.InvokeAsync(new ToolInvocation(
                "create_plan",
                new Dictionary<string, string>
                {
                    ["name"] = "Two step",
                    ["description"] = "d",
                    ["expected_outcome"] = "o",
                    ["subtasks"] = """[{"name":"One","description":"","expected_outcome":""},{"name":"Two","description":"","expected_outcome":""}]"""
                }));

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

    private static ToolRouter CreateRouter(
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

        return new ToolRouter(tools);
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
