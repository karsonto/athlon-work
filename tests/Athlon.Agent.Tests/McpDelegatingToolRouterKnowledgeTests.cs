using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpDelegatingToolRouterKnowledgeTests
{
    [Fact]
    public void ListTools_HidesKnowledgeSearch_WhenSessionDisabled()
    {
        var router = CreateRouter(enabled: false, moduleIds: ["module-a"]);
        Assert.DoesNotContain(router.ListTools(), tool => tool.Name == "knowledge_search");
    }

    [Fact]
    public void ListTools_HidesKnowledgeSearch_WhenEnabledWithoutModules()
    {
        var router = CreateRouter(enabled: true, moduleIds: []);
        Assert.DoesNotContain(router.ListTools(), tool => tool.Name == "knowledge_search");
    }

    [Fact]
    public void ListTools_ExposesKnowledgeSearch_WhenEnabledWithModules()
    {
        var router = CreateRouter(enabled: true, moduleIds: ["module-a"]);
        Assert.Contains(router.ListTools(), tool => tool.Name == "knowledge_search");
    }

    private static McpDelegatingToolRouter CreateRouter(bool enabled, IReadOnlyList<string> moduleIds)
    {
        return new McpDelegatingToolRouter(
            static tools => tools,
            [new StubKnowledgeTool()],
            new TestMcpRegistry(),
            new AppSettings(),
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(enabled, moduleIds.ToArray()));
    }

    private sealed class StubKnowledgeTool : IAgentTool, IGlobalKnowledgeTool
    {
        public ToolDefinition Definition { get; } = new(
            "knowledge_search",
            "Search knowledge base",
            new Dictionary<string, string> { ["query"] = "query" });

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
