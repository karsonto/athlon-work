using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

public sealed class PlanToolCatalogTests
{
    private static readonly ToolDefinition[] SampleTools =
    [
        new ToolDefinition("file_read", "read", new Dictionary<string, string>()),
        new ToolDefinition("file_write", "write", new Dictionary<string, string>()),
        new ToolDefinition("file_edit", "edit", new Dictionary<string, string>()),
        new ToolDefinition("execute_command", "run", new Dictionary<string, string>()),
        new ToolDefinition(PlanToolCatalog.CreatePlan, "create", new Dictionary<string, string>()),
        new ToolDefinition(PlanToolCatalog.FinishSubtask, "finish", new Dictionary<string, string>()),
        new ToolDefinition(PlanToolCatalog.GetPlan, "get", new Dictionary<string, string>())
    ];

    [Fact]
    public void FilterForSession_AgentModeWithoutPlan_ExcludesPlanTools()
    {
        var filtered = PlanToolCatalog.FilterForSession(SampleTools, AgentInteractionMode.Agent, plan: null);

        Assert.DoesNotContain(filtered, tool => PlanToolCatalog.IsPlanTool(tool.Name));
        Assert.Contains(filtered, tool => tool.Name == "file_write");
    }

    [Fact]
    public void FilterForSession_PlanMode_ExcludesMutatingAndFinishTools()
    {
        var filtered = PlanToolCatalog.FilterForSession(SampleTools, AgentInteractionMode.Plan, plan: null);

        Assert.Contains(filtered, tool => tool.Name == "file_read");
        Assert.Contains(filtered, tool => tool.Name == PlanToolCatalog.CreatePlan);
        Assert.Contains(filtered, tool => tool.Name == PlanToolCatalog.GetPlan);
        Assert.DoesNotContain(filtered, tool => tool.Name == "file_write");
        Assert.DoesNotContain(filtered, tool => tool.Name == "execute_command");
        Assert.DoesNotContain(filtered, tool => tool.Name == PlanToolCatalog.FinishSubtask);
    }

    [Fact]
    public void FilterForSession_AgentModeWithApprovedPlan_IncludesExecutionPlanTools()
    {
        var plan = PlanTestFixtures.SampleAgentPlan(PlanPhase.Approved);
        var filtered = PlanToolCatalog.FilterForSession(SampleTools, AgentInteractionMode.Agent, plan);

        Assert.Contains(filtered, tool => tool.Name == PlanToolCatalog.GetPlan);
        Assert.Contains(filtered, tool => tool.Name == PlanToolCatalog.FinishSubtask);
        Assert.DoesNotContain(filtered, tool => tool.Name == PlanToolCatalog.CreatePlan);
    }

    [Fact]
    public void FilterForSession_AgentModeWithDraftPlan_ExcludesPlanTools()
    {
        var plan = PlanTestFixtures.SampleAgentPlan(PlanPhase.Draft);
        var filtered = PlanToolCatalog.FilterForSession(SampleTools, AgentInteractionMode.Agent, plan);

        Assert.DoesNotContain(filtered, tool => PlanToolCatalog.IsPlanTool(tool.Name));
    }

    [Theory]
    [InlineData(PlanToolCatalog.CreatePlan, true)]
    [InlineData(PlanToolCatalog.FinishSubtask, true)]
    [InlineData(PlanToolCatalog.GetPlan, true)]
    [InlineData("file_read", false)]
    public void IsPlanTool_RecognizesPlanToolNames(string name, bool expected) =>
        Assert.Equal(expected, PlanToolCatalog.IsPlanTool(name));
}
