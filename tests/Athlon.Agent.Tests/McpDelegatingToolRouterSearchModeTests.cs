using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpDelegatingToolRouterSearchModeTests
{
    [Fact]
    public void ListTools_uses_gateway_tools_when_threshold_exceeded()
    {
        var catalog = Enumerable.Range(0, 15)
            .Select(index => new McpCatalogEntry(
                "server",
                $"tool_{index}",
                McpToolNameCodec.Encode("server", $"tool_{index}"),
                $"tool {index}",
                "{}"))
            .ToArray();

        var registry = new TestMcpRegistry(catalog);
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings { Enabled = true, Mode = "auto", AutoThresholdToolCount = 12 }
        };

        var router = new McpDelegatingToolRouter(
            static tools => tools,
            Array.Empty<IAgentTool>(),
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState());

        var tools = router.ListTools();

        Assert.Contains(tools, tool => tool.Name == McpSearchGatewayTools.SearchToolName);
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("mcp_server__tool_", StringComparison.Ordinal));
    }

}
