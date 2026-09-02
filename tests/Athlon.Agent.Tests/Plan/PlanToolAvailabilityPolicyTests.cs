using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Tests.Plan;

public sealed class PlanToolAvailabilityPolicyTests
{
    private static ToolAvailabilityContext PlanCtx(PlanPhase phase) => new(
        ComputerUseActive: false,
        HasWorkspace: true,
        WorkspaceKind: WorkspaceKind.Local,
        Mode: SessionAgentMode.Plan,
        BrowserTabOpen: false,
        TerminalTabOpen: false,
        KnowledgeEnabled: false,
        ActivePlanPhase: phase);

    [Theory]
    [InlineData(PlanPhase.Explore, false)]
    [InlineData(PlanPhase.Draft, false)]
    [InlineData(PlanPhase.AwaitConfirm, false)]
    [InlineData(PlanPhase.AwaitClarify, false)]
    public void PlanMode_BlocksWrites(PlanPhase phase, bool expectedWrite)
    {
        var ctx = PlanCtx(phase);
        Assert.Equal(expectedWrite, ToolAvailabilityPolicy.IsEnabled(new StubLocalWrite("file_write"), ctx));
    }

    [Theory]
    [InlineData(PlanPhase.Explore, false)]
    [InlineData(PlanPhase.Draft, true)]
    [InlineData(PlanPhase.AwaitConfirm, false)]
    [InlineData(PlanPhase.AwaitClarify, false)]
    public void PlanMode_PublishPlan_DraftOnly(PlanPhase phase, bool expected)
    {
        var ctx = PlanCtx(phase);
        Assert.Equal(expected, ToolAvailabilityPolicy.IsEnabled(new StubPlanDocument("publish_plan"), ctx));
    }

    [Fact]
    public void PlanMode_BlocksShellAndSubAgents()
    {
        var ctx = PlanCtx(PlanPhase.Draft);
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubLocalWrite("execute_command"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubSubAgent("sessions_spawn"), ctx));
    }

    [Theory]
    [InlineData(PlanPhase.Explore, true)]
    [InlineData(PlanPhase.Draft, false)]
    [InlineData(PlanPhase.AwaitConfirm, false)]
    [InlineData(PlanPhase.AwaitClarify, false)]
    public void PlanMode_AskClarification_ExploreOnly(PlanPhase phase, bool expected)
    {
        var ctx = PlanCtx(phase);
        Assert.Equal(expected, ToolAvailabilityPolicy.IsEnabled(new StubPlanClarify("ask_plan_clarification"), ctx));
    }

    [Fact]
    public void Classifier_TagsPlanDocumentFacet()
    {
        Assert.True(ToolFacetClassifier.Classify(new StubPlanDocument("publish_plan")).HasFlag(ToolFacet.PlanDocument));
        Assert.False(ToolFacetClassifier.Classify(new StubNamed("file_read")).HasFlag(ToolFacet.PlanDocument));
    }

    [Fact]
    public void Classifier_TagsPlanClarifyFacet()
    {
        Assert.True(ToolFacetClassifier.Classify(new StubPlanClarify("ask_plan_clarification")).HasFlag(ToolFacet.PlanClarify));
        Assert.False(ToolFacetClassifier.Classify(new StubPlanDocument("publish_plan")).HasFlag(ToolFacet.PlanClarify));
    }

    private sealed class StubNamed(string name) : IAgentTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubLocalWrite(string name) : IAgentTool, ILocalWorkspaceTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubPlanClarify(string name) : IAgentTool, IPlanClarifyTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubPlanDocument(string name) : IAgentTool, IPlanDocumentTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubSubAgent(string name) : IAgentTool, ISubAgentTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
