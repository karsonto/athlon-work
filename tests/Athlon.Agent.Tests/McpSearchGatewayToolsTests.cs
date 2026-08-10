using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpSearchGatewayToolsTests
{
    [Fact]
    public async Task McpSearch_ReturnsReadableChineseInToolContent()
    {
        var catalog = new[]
        {
            new McpCatalogEntry(
                "server",
                "search_tool",
                McpToolNameCodec.Encode("server", "search_tool"),
                "搜索工具描述",
                """{"type":"object","properties":{"query":{"type":"string","description":"查询关键词"}}}""")
        };

        var registry = new TestMcpRegistry(catalog);
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = "search",
                MinScore = 0.01
            }
        };

        var router = new McpDelegatingToolRouter(
            static tools => tools,
            Array.Empty<IAgentTool>(),
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard(),
            RouterTestDependencies.CreateBrowserWorkspaceState());

        var result = await router.InvokeAsync(new ToolInvocation(
            McpSearchGatewayTools.SearchToolName,
            ToolCallArgumentsParser.ParseJson("""{"query":"搜索"}""")));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Content);
        Assert.Contains("搜索工具描述", result.Content);
        Assert.Contains("搜索", result.Content);
        Assert.DoesNotContain(@"\u641c", result.Content);
        Assert.DoesNotContain(@"\u5de5", result.Content);
    }
}
