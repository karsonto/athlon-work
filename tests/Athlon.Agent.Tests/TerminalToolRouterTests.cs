using Athlon.Agent.Core;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Terminal;

namespace Athlon.Agent.Tests;

public sealed class TerminalToolRouterTests
{
    [Fact]
    public void ListTools_WithoutTerminalTab_IncludesOpen_ExcludesSessionTools()
    {
        var router = CreateRouter(hasTerminalTab: false);
        var names = router.ListTools().Select(t => t.Name).ToArray();
        Assert.Contains("terminal_open", names);
        Assert.DoesNotContain("terminal_send_input", names);
        Assert.DoesNotContain("terminal_read_output", names);
        Assert.DoesNotContain("terminal_get_session_info", names);
    }

    [Fact]
    public void ListTools_WithTerminalTab_IncludesAllTerminalTools()
    {
        var router = CreateRouter(hasTerminalTab: true);
        var names = router.ListTools().Select(t => t.Name).ToArray();
        Assert.Contains("terminal_open", names);
        Assert.Contains("terminal_send_input", names);
        Assert.Contains("terminal_read_output", names);
        Assert.Contains("terminal_get_session_info", names);
    }

    [Fact]
    public void ListTools_ChatOnly_StillIncludesOpen()
    {
        var router = CreateRouter(hasTerminalTab: false, configuredWorkspace: false);
        var names = router.ListTools().Select(t => t.Name).ToArray();
        Assert.Contains("terminal_open", names);
        Assert.DoesNotContain("terminal_send_input", names);
    }

    [Fact]
    public void ListTools_ChatOnly_WithTerminalTab_IncludesSessionTools()
    {
        var router = CreateRouter(hasTerminalTab: true, configuredWorkspace: false);
        var names = router.ListTools().Select(t => t.Name).ToArray();
        Assert.Contains("terminal_open", names);
        Assert.Contains("terminal_send_input", names);
        Assert.Contains("terminal_read_output", names);
    }

    [Fact]
    public void ListTools_TerminalTabToggle_RefreshesSessionToolAvailability()
    {
        var terminalState = new MutableTerminalWorkspaceState(hasOpenTerminalTab: false);
        var router = CreateRouter(terminalState, configuredWorkspace: true);

        Assert.DoesNotContain("terminal_send_input", router.ListTools().Select(t => t.Name));

        terminalState.HasOpenTerminalTab = true;
        Assert.Contains("terminal_send_input", router.ListTools().Select(t => t.Name));
    }

    [Fact]
    public async Task OpenTool_NullHost_ReturnsFailure()
    {
        var tool = new TerminalOpenTool(NullTerminalAutomationHost.Instance);
        var result = await tool.InvokeAsync(
            new ToolInvocation("terminal_open", ToolCallArguments.Empty));

        Assert.False(result.Succeeded);
        Assert.Contains("not available", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendInput_NullHost_ReturnsFailure()
    {
        var tool = new TerminalSendInputTool(NullTerminalAutomationHost.Instance);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "terminal_send_input",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["text"] = "hello"
                })));

        Assert.False(result.Succeeded);
    }

    private static McpDelegatingToolRouter CreateRouter(bool hasTerminalTab, bool configuredWorkspace = true) =>
        CreateRouter(RouterTestDependencies.CreateTerminalWorkspaceState(hasTerminalTab), configuredWorkspace);

    private static McpDelegatingToolRouter CreateRouter(
        ITerminalWorkspaceState terminalWorkspaceState,
        bool configuredWorkspace = true)
    {
        IAgentTool[] tools =
        [
            new TerminalOpenTool(NullTerminalAutomationHost.Instance),
            new TerminalSendInputTool(NullTerminalAutomationHost.Instance),
            new TerminalReadOutputTool(NullTerminalAutomationHost.Instance),
            new TerminalGetSessionInfoTool(NullTerminalAutomationHost.Instance),
        ];

        return new McpDelegatingToolRouter(
            static t => t,
            tools,
            new TestMcpRegistry(),
            new AppSettings(),
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard(configuredWorkspace),
            RouterTestDependencies.CreateBrowserWorkspaceState(),
            terminalWorkspaceState);
    }

    private sealed class MutableTerminalWorkspaceState(bool hasOpenTerminalTab) : ITerminalWorkspaceState
    {
        public bool HasOpenTerminalTab { get; set; } = hasOpenTerminalTab;
    }
}
