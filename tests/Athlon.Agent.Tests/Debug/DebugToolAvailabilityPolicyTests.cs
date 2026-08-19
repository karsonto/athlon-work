using Athlon.Agent.Core;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Tests.Debug;

public sealed class DebugToolAvailabilityPolicyTests
{
    private static ToolAvailabilityContext DebugCtx(DebugPhase phase) => new(
        ComputerUseActive: false,
        HasWorkspace: true,
        WorkspaceKind: WorkspaceKind.Local,
        Mode: SessionAgentMode.Debug,
        BrowserTabOpen: false,
        TerminalTabOpen: false,
        KnowledgeEnabled: false,
        ActiveDebugPhase: phase);

    [Theory]
    [InlineData(DebugPhase.Hypothesize, false)]
    [InlineData(DebugPhase.Instrument, true)]
    [InlineData(DebugPhase.Analyze, false)]
    [InlineData(DebugPhase.AwaitRepro, false)]
    [InlineData(DebugPhase.AwaitFixConfirm, false)]
    [InlineData(DebugPhase.Fix, true)]
    public void DebugMode_WriteGatedByPhase(DebugPhase phase, bool expectedWrite)
    {
        var ctx = DebugCtx(phase);
        Assert.Equal(expectedWrite, ToolAvailabilityPolicy.IsEnabled(new StubLocalWrite("file_write"), ctx));
    }

    [Fact]
    public void DebugMode_BlocksShellAndSubAgents()
    {
        var ctx = DebugCtx(DebugPhase.Instrument);
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubLocalWrite("execute_command"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubSubAgent("sessions_spawn"), ctx));
    }

    [Fact]
    public void Classifier_TagsShellFacet()
    {
        Assert.True(ToolFacetClassifier.Classify(new StubNamed("execute_command")).HasFlag(ToolFacet.Shell));
        Assert.False(ToolFacetClassifier.Classify(new StubNamed("file_write")).HasFlag(ToolFacet.Shell));
    }

    private sealed class StubNamed(string name) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubLocalWrite(string name) : IAgentTool, ILocalWorkspaceTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubSubAgent(string name) : IAgentTool, ISubAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
