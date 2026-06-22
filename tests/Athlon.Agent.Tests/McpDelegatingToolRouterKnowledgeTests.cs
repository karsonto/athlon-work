using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;

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

    [Fact]
    public void ListTools_ChatOnly_NoKnowledge_ReturnsEmpty()
    {
        var router = CreateRouter(enabled: false, moduleIds: [], configuredWorkspace: false, includeFileTool: true);
        Assert.Empty(router.ListTools());
    }

    [Fact]
    public void ListTools_ChatOnly_WithKnowledge_ReturnsOnlyKnowledgeSearch()
    {
        var router = CreateRouter(enabled: true, moduleIds: ["module-a"], configuredWorkspace: false, includeFileTool: true);
        var names = router.ListTools().Select(tool => tool.Name).ToArray();
        Assert.Equal(["knowledge_search"], names);
    }

    [Fact]
    public void ListTools_ChatOnly_ExcludesMcpTools()
    {
        var catalog = new[]
        {
            new McpCatalogEntry("server", "ping", McpToolNameCodec.Encode("server", "ping"), "ping", "{}")
        };
        var router = CreateRouter(
            enabled: false,
            moduleIds: [],
            configuredWorkspace: false,
            mcpRegistry: new TestMcpRegistry(catalog));
        Assert.Empty(router.ListTools());
    }

    [Fact]
    public async Task InvokeAsync_ChatOnly_RejectsMcpTool()
    {
        var catalog = new[]
        {
            new McpCatalogEntry("server", "ping", McpToolNameCodec.Encode("server", "ping"), "ping", "{}")
        };
        var router = CreateRouter(
            enabled: false,
            moduleIds: [],
            configuredWorkspace: false,
            mcpRegistry: new TestMcpRegistry(catalog));

        var result = await router.InvokeAsync(new ToolInvocation(McpToolNameCodec.Encode("server", "ping"), new Dictionary<string, string>()));

        Assert.False(result.Succeeded);
        Assert.Contains("not available", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListTools_WithWorkspace_IncludesFileAndKnowledgeTools()
    {
        var router = CreateRouter(enabled: true, moduleIds: ["module-a"], configuredWorkspace: true, includeFileTool: true);
        var names = router.ListTools().Select(tool => tool.Name).ToArray();
        Assert.Contains("knowledge_search", names);
        Assert.Contains("file_list", names);
    }

    private static McpDelegatingToolRouter CreateRouter(
        bool enabled,
        IReadOnlyList<string> moduleIds,
        bool configuredWorkspace = true,
        bool includeFileTool = false,
        IMcpRegistry? mcpRegistry = null)
    {
        IAgentTool[] tools = includeFileTool
            ? [new StubKnowledgeTool(), new StubNamedTool("file_list")]
            : [new StubKnowledgeTool()];

        return new McpDelegatingToolRouter(
            static tools => tools,
            tools,
            mcpRegistry ?? new TestMcpRegistry(),
            new AppSettings(),
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(enabled, moduleIds.ToArray()),
            RouterTestDependencies.CreateWorkspaceGuard(configuredWorkspace));
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

    private sealed class StubNamedTool(string name) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, name, new Dictionary<string, string>());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
