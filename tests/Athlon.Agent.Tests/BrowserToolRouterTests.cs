using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Browser;

namespace Athlon.Agent.Tests;

public sealed class BrowserToolRouterTests
{
    [Fact]
    public void ListTools_WithoutBrowserTab_IncludesNavigate_ExcludesAria()
    {
        var router = CreateRouter(hasBrowserTab: false);
        var names = router.ListTools().Select(t => t.Name).ToArray();
        Assert.Contains("browser_navigate", names);
        Assert.DoesNotContain("browser_get_page_info", names);
        Assert.DoesNotContain("browser_read_aria_tree", names);
        Assert.DoesNotContain("browser_find_aria_nodes", names);
        Assert.DoesNotContain("browser_aria_interact", names);
        Assert.DoesNotContain("browser_network_list", names);
        Assert.DoesNotContain("browser_network_get", names);
        Assert.DoesNotContain("browser_console_read", names);
    }

    [Fact]
    public void ListTools_WithBrowserTab_IncludesNavigateAndAria()
    {
        var router = CreateRouter(hasBrowserTab: true);
        var names = router.ListTools().Select(t => t.Name).ToArray();
        Assert.Contains("browser_navigate", names);
        Assert.Contains("browser_get_page_info", names);
        Assert.Contains("browser_read_aria_tree", names);
        Assert.Contains("browser_find_aria_nodes", names);
        Assert.Contains("browser_resolve_aria_ref", names);
        Assert.Contains("browser_aria_inspect", names);
        Assert.Contains("browser_aria_interact", names);
        Assert.Contains("browser_wait_for_aria", names);
        Assert.Contains("browser_network_list", names);
        Assert.Contains("browser_network_get", names);
        Assert.Contains("browser_console_read", names);
    }

    [Fact]
    public void ListTools_ChatOnly_StillIncludesNavigate()
    {
        var router = CreateRouter(hasBrowserTab: false, configuredWorkspace: false);
        var names = router.ListTools().Select(t => t.Name).ToArray();
        Assert.Contains("browser_navigate", names);
        Assert.DoesNotContain("browser_read_aria_tree", names);
    }

    [Fact]
    public void ListTools_ChatOnly_WithBrowserTab_IncludesAria()
    {
        var router = CreateRouter(hasBrowserTab: true, configuredWorkspace: false);
        var names = router.ListTools().Select(t => t.Name).ToArray();
        Assert.Contains("browser_navigate", names);
        Assert.Contains("browser_read_aria_tree", names);
        Assert.Contains("browser_find_aria_nodes", names);
        Assert.Contains("browser_get_page_info", names);
    }

    [Fact]
    public void ListTools_BrowserTabToggle_RefreshesAriaAvailability()
    {
        var browserState = new MutableBrowserWorkspaceState(hasOpenBrowserTab: false);
        var router = CreateRouter(browserState, configuredWorkspace: true);

        Assert.DoesNotContain("browser_read_aria_tree", router.ListTools().Select(t => t.Name));

        browserState.HasOpenBrowserTab = true;
        Assert.Contains("browser_read_aria_tree", router.ListTools().Select(t => t.Name));
    }

    [Fact]
    public async Task NavigateTool_NullHost_ReturnsFailure()
    {
        var tool = new BrowserNavigateTool(NullBrowserAutomationHost.Instance);
        var result = await tool.InvokeAsync(
            new ToolInvocation("browser_navigate", ToolCallArguments.FromStrings(new Dictionary<string, string>
            {
                ["url"] = "https://example.com"
            })));

        Assert.False(result.Succeeded);
        Assert.Contains("not available", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAriaTree_NullHost_ReturnsFailure()
    {
        var tool = new BrowserReadAriaTreeTool(NullBrowserAutomationHost.Instance);
        var result = await tool.InvokeAsync(new ToolInvocation("browser_read_aria_tree", ToolCallArguments.Empty));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task FindAriaNodes_LimitOnly_FailsBeforeHost()
    {
        var tool = new BrowserFindAriaNodesTool(NullBrowserAutomationHost.Instance);
        var result = await tool.InvokeAsync(
            new ToolInvocation(
                "browser_find_aria_nodes",
                ToolCallArguments.FromStrings(new Dictionary<string, string>
                {
                    ["limit"] = "10"
                })));

        Assert.False(result.Succeeded);
        Assert.Equal("Invalid ARIA arguments", result.Summary);
        Assert.Contains("name, role, text", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkGet_MissingRequestId_FailsBeforeHost()
    {
        var tool = new BrowserNetworkGetTool(NullBrowserAutomationHost.Instance);
        var result = await tool.InvokeAsync(
            new ToolInvocation("browser_network_get", ToolCallArguments.Empty));

        Assert.False(result.Succeeded);
        Assert.Equal("Missing requestId", result.Summary);
    }

    [Fact]
    public void AriaHostScript_IsEmbeddedOrCopied()
    {
        var script = Athlon.Agent.App.Services.Browser.BrowserAutomationHost.TryLoadAriaHostScript();
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("__athlonAria", script, StringComparison.Ordinal);
        Assert.Contains("__version", script, StringComparison.Ordinal);
        Assert.Contains("readAriaTree", script, StringComparison.Ordinal);
        Assert.Contains("filter", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeUrl_AddsHttps_AndSearchesOnSpaces()
    {
        Assert.Equal(
            "https://example.com",
            Athlon.Agent.App.ViewModels.BrowserWorkspaceTabViewModel.NormalizeUrl("example.com"));
        var search = Athlon.Agent.App.ViewModels.BrowserWorkspaceTabViewModel.NormalizeUrl("hello world");
        Assert.StartsWith("https://www.bing.com/search?q=", search, StringComparison.Ordinal);
    }

    private static McpDelegatingToolRouter CreateRouter(bool hasBrowserTab, bool configuredWorkspace = true) =>
        CreateRouter(RouterTestDependencies.CreateBrowserWorkspaceState(hasBrowserTab), configuredWorkspace);

    private static McpDelegatingToolRouter CreateRouter(
        IBrowserWorkspaceState browserWorkspaceState,
        bool configuredWorkspace = true)
    {
        IAgentTool[] tools =
        [
            new BrowserNavigateTool(NullBrowserAutomationHost.Instance),
            new BrowserGetPageInfoTool(NullBrowserAutomationHost.Instance),
            new BrowserReadAriaTreeTool(NullBrowserAutomationHost.Instance),
            new BrowserFindAriaNodesTool(NullBrowserAutomationHost.Instance),
            new BrowserResolveAriaRefTool(NullBrowserAutomationHost.Instance),
            new BrowserAriaInspectTool(NullBrowserAutomationHost.Instance),
            new BrowserAriaInteractTool(NullBrowserAutomationHost.Instance),
            new BrowserWaitForAriaTool(NullBrowserAutomationHost.Instance),
            new BrowserNetworkListTool(NullBrowserAutomationHost.Instance),
            new BrowserNetworkGetTool(NullBrowserAutomationHost.Instance),
            new BrowserConsoleReadTool(NullBrowserAutomationHost.Instance),
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
            RouterTestDependencies.CreateDebugPhaseAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard(configuredWorkspace),
            browserWorkspaceState,
            RouterTestDependencies.CreateTerminalWorkspaceState());
    }

    private sealed class MutableBrowserWorkspaceState(bool hasOpenBrowserTab) : IBrowserWorkspaceState
    {
        public bool HasOpenBrowserTab { get; set; } = hasOpenBrowserTab;
    }
}
