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
    public void NormalizeUrl_AddsHttps_AndSearchesOnSpaces()
    {
        Assert.Equal(
            "https://example.com/",
            Athlon.Agent.App.ViewModels.BrowserWorkspaceTabViewModel.NormalizeUrl("example.com"));
        var search = Athlon.Agent.App.ViewModels.BrowserWorkspaceTabViewModel.NormalizeUrl("hello world");
        Assert.StartsWith("https://www.bing.com/search?q=", search, StringComparison.Ordinal);
    }

    private static McpDelegatingToolRouter CreateRouter(bool hasBrowserTab, bool configuredWorkspace = true)
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
            RouterTestDependencies.CreateBrowserWorkspaceState(hasBrowserTab));
    }
}
