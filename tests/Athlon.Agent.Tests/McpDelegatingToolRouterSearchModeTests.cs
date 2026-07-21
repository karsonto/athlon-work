using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpDelegatingToolRouterSearchModeTests
{
    [Theory]
    [InlineData("direct", 20, false)]
    [InlineData("search", 1, true)]
    [InlineData("auto", 1, false)]
    [InlineData("auto", 20, true)]
    public void ListTools_RespectsDirectSearchAndAutoModes(
        string mode,
        int toolCount,
        bool expectsSearchGateway)
    {
        var registry = new TestMcpRegistry(CreateCatalog(toolCount));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = mode,
                AutoThresholdToolCount = 12,
                AutoThresholdSchemaChars = int.MaxValue
            }
        };

        var tools = CreateRouter(registry, settings).ListTools();

        Assert.Equal(
            expectsSearchGateway,
            tools.Any(tool => tool.Name == McpSearchGatewayTools.SearchToolName));
        Assert.Equal(
            !expectsSearchGateway,
            tools.Any(tool => tool.Name.StartsWith("mcp_server__", StringComparison.Ordinal)));
    }

    [Fact]
    public void ListTools_uses_gateway_tools_when_threshold_exceeded()
    {
        var catalog = CreateCatalog(15);

        var registry = new TestMcpRegistry(catalog);
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings { Enabled = true, Mode = "auto", AutoThresholdToolCount = 12 }
        };

        var router = CreateRouter(registry, settings);

        var tools = router.ListTools();

        Assert.Contains(tools, tool => tool.Name == McpSearchGatewayTools.SearchToolName);
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("mcp_server__tool_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("search", true)]
    public async Task InvokeAsync_GatewayAvailabilityMatchesMode(string mode, bool expectedSuccess)
    {
        var registry = new TestMcpRegistry(CreateCatalog(1));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = mode,
                MinScore = 0.01
            }
        };
        var router = CreateRouter(registry, settings);

        var result = await router.InvokeAsync(new ToolInvocation(
            McpSearchGatewayTools.SearchToolName,
            ToolCallArgumentsParser.ParseJson("""{"query":"tool"}""")));

        Assert.Equal(expectedSuccess, result.Succeeded);
    }

    [Fact]
    public void ListTools_AutoMode_StickySearch_UsesHysteresisBeforeExit()
    {
        var registry = new TestMcpRegistry(CreateCatalog(15));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = "auto",
                AutoThresholdToolCount = 12,
                AutoThresholdSchemaChars = int.MaxValue,
                AutoHysteresisToolCount = 3,
                AutoHysteresisSchemaChars = 0
            }
        };
        var router = CreateRouter(registry, settings);

        Assert.Contains(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);

        // Still above exit band (12 - 3 = 9): stay in search.
        registry.SetCatalog(CreateCatalog(10));
        Assert.Contains(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);

        // Below exit band: leave search.
        registry.SetCatalog(CreateCatalog(8));
        Assert.DoesNotContain(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);
        Assert.Contains(router.ListTools(), tool => tool.Name.StartsWith("mcp_server__", StringComparison.Ordinal));
    }

    [Fact]
    public void ListTools_DirectMode_IgnoresStickySearch()
    {
        var registry = new TestMcpRegistry(CreateCatalog(15));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = "auto",
                AutoThresholdToolCount = 12,
                AutoThresholdSchemaChars = int.MaxValue
            }
        };
        var router = CreateRouter(registry, settings);
        Assert.Contains(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);

        settings.McpSearch.Mode = "direct";
        Assert.DoesNotContain(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);
        Assert.Contains(router.ListTools(), tool => tool.Name.StartsWith("mcp_server__", StringComparison.Ordinal));
    }

    private static McpDelegatingToolRouter CreateRouter(
        IMcpRegistry registry,
        AppSettings settings) =>
        new(
            static tools => tools,
            Array.Empty<IAgentTool>(),
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard());

    private static McpCatalogEntry[] CreateCatalog(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new McpCatalogEntry(
                "server",
                $"tool_{index}",
                McpToolNameCodec.Encode("server", $"tool_{index}"),
                $"tool {index}",
                """{"type":"object","properties":{}}"""))
            .ToArray();
}
