using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Tests;

public sealed class ToolAvailabilityPolicyTests
{
    private static readonly ToolAvailabilityContext AgentLocal = new(
        ComputerUseActive: false,
        HasWorkspace: true,
        WorkspaceKind: WorkspaceKind.Local,
        Mode: SessionAgentMode.Agent,
        BrowserTabOpen: false,
        TerminalTabOpen: false,
        KnowledgeEnabled: false);

    [Fact]
    public void ComputerUseActive_AllowsOnlyComputerUseTools()
    {
        var ctx = AgentLocal with { ComputerUseActive = true };
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubComputerUse("computer_observe"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubLocal("file_list"), ctx));
    }

    [Fact]
    public void AgentMode_ExcludesComputerUseTools()
    {
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubComputerUse("computer_observe"), AgentLocal));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubLocal("file_list"), AgentLocal));
    }

    [Fact]
    public void ChatOnly_AllowsBootstrapAndConditionalBrowserTerminalKnowledge()
    {
        var ctx = AgentLocal with { HasWorkspace = false, KnowledgeEnabled = true, BrowserTabOpen = true };

        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubNamed("browser_navigate"), ctx));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubNamed("terminal_open"), ctx));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubBrowser("browser_read_aria_tree"), ctx));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubKnowledge("knowledge_search"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubLocal("file_list"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubMemory("memory_search"), ctx));
    }

    [Fact]
    public void ChatOnly_WithoutTab_ExcludesNonBootstrapBrowserTools()
    {
        var ctx = AgentLocal with { HasWorkspace = false, BrowserTabOpen = false };
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubNamed("browser_navigate"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubBrowser("browser_read_aria_tree"), ctx));
    }

    [Fact]
    public void SshWorkspace_ExcludesLocalAllowsRemote()
    {
        var ctx = AgentLocal with { WorkspaceKind = WorkspaceKind.Ssh };
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubLocal("file_list"), ctx));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubRemote("file_list"), ctx));
    }

    [Fact]
    public void LocalWorkspace_ExcludesRemote()
    {
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubRemote("file_list"), AgentLocal));
    }

    [Theory]
    [InlineData(SessionAgentMode.Agent, false)]
    [InlineData(SessionAgentMode.Coding, true)]
    [InlineData(SessionAgentMode.Ask, false)]
    [InlineData(SessionAgentMode.Debug, false)]
    public void TodoWrite_OnlyInCoding(SessionAgentMode mode, bool expected)
    {
        var ctx = AgentLocal with { Mode = mode };
        Assert.Equal(expected, ToolAvailabilityPolicy.IsEnabled(new StubHarness("todo_write"), ctx));
    }

    [Fact]
    public void Ask_BlocksWritesShellTerminalAndSubAgents()
    {
        var ctx = AgentLocal with { Mode = SessionAgentMode.Ask, TerminalTabOpen = true };
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubLocalWrite("file_write"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubLocalWrite("execute_command"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubSubAgent("sessions_spawn"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubNamed("terminal_open"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubTerminal("terminal_send_input"), ctx));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubTerminal("terminal_read_output"), ctx));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubLocal("file_read"), ctx));
    }

    [Fact]
    public void Ask_ChatOnly_BlocksTerminalBootstrap()
    {
        var ctx = AgentLocal with { Mode = SessionAgentMode.Ask, HasWorkspace = false };
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubNamed("terminal_open"), ctx));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubNamed("browser_navigate"), ctx));
    }

    [Fact]
    public void BrowserAndTerminal_RequireOpenTabs_ExceptBootstrap()
    {
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubNamed("browser_navigate"), AgentLocal));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubNamed("terminal_open"), AgentLocal));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubBrowser("browser_get_page_info"), AgentLocal));
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubTerminal("terminal_send_input"), AgentLocal));

        var open = AgentLocal with { BrowserTabOpen = true, TerminalTabOpen = true };
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubBrowser("browser_get_page_info"), open));
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubTerminal("terminal_send_input"), open));
    }

    [Fact]
    public void Knowledge_RequiresSessionFlag()
    {
        Assert.False(ToolAvailabilityPolicy.IsEnabled(new StubKnowledge("knowledge_search"), AgentLocal));
        var enabled = AgentLocal with { KnowledgeEnabled = true };
        Assert.True(ToolAvailabilityPolicy.IsEnabled(new StubKnowledge("knowledge_search"), enabled));
    }

    [Fact]
    public void Classifier_TagsWriteAndBootstrapNames()
    {
        Assert.True(ToolFacetClassifier.Classify(new StubNamed("browser_navigate")).HasFlag(ToolFacet.BrowserBootstrap));
        Assert.True(ToolFacetClassifier.Classify(new StubNamed("terminal_open")).HasFlag(ToolFacet.TerminalBootstrap));
        Assert.True(ToolFacetClassifier.Classify(new StubNamed("file_write")).HasFlag(ToolFacet.WriteFileOrShell));
        Assert.False(ToolFacetClassifier.Classify(new StubNamed("file_read")).HasFlag(ToolFacet.WriteFileOrShell));
        Assert.True(ToolFacetClassifier.Classify(new StubNamed("execute_command")).HasFlag(ToolFacet.Shell));
    }

    private sealed class StubNamed(string name) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubLocal(string name) : IAgentTool, ILocalWorkspaceTool
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

    private sealed class StubRemote(string name) : IAgentTool, IRemoteWorkspaceTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubComputerUse(string name) : IAgentTool, IComputerUseTool
    {
        public ToolDefinition Definition { get; } =
            new(name, name, ToolSchema.Object().Build(), Source: "computer-use");

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubBrowser(string name) : IAgentTool, IBrowserTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubTerminal(string name) : IAgentTool, ITerminalTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubHarness(string name) : IAgentTool, IHarnessTool
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

    private sealed class StubMemory(string name) : IAgentTool, ILongTermMemoryTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubKnowledge(string name) : IAgentTool, IGlobalKnowledgeTool
    {
        public ToolDefinition Definition { get; } = new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
