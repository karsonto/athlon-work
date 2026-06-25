using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class CompositeToolRouterHarnessTests
{
    [Fact]
    public void ListTools_WhenHarnessDisabled_ExcludesHarnessTools()
    {
        var router = CreateRouter(harnessEnabled: false);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.DoesNotContain("memory_search", names);
        Assert.DoesNotContain("memory_get", names);
        Assert.DoesNotContain("todo_write", names);
        Assert.Contains("file_list", names);
    }

    [Fact]
    public void ListTools_WhenHarnessEnabled_IncludesHarnessTools()
    {
        var router = CreateRouter(harnessEnabled: true);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("memory_search", names);
        Assert.Contains("memory_get", names);
        Assert.Contains("todo_write", names);
        Assert.Contains("file_list", names);
    }

    [Fact]
    public async Task InvokeAsync_WhenHarnessDisabled_ReturnsNotFoundForHarnessTools()
    {
        var router = CreateRouter(harnessEnabled: false);

        var searchResult = await router.InvokeAsync(new ToolInvocation("memory_search", new Dictionary<string, string> { ["query"] = "test" }));
        var todoResult = await router.InvokeAsync(new ToolInvocation("todo_write", new Dictionary<string, string>
        {
            ["todos"] = """[{"id":"1","content":"x","status":"pending"}]""",
            ["merge"] = "false"
        }));

        Assert.False(searchResult.Succeeded);
        Assert.False(todoResult.Succeeded);
    }

    [Fact]
    public async Task InvokeAsync_WhenHarnessEnabled_ResolvesMemoryTools()
    {
        var router = CreateRouter(harnessEnabled: true);

        var result = await router.InvokeAsync(new ToolInvocation("memory_search", new Dictionary<string, string> { ["query"] = "nonexistent-xyz-123" }));

        Assert.True(result.Succeeded);
    }

    private static CompositeToolRouter CreateRouter(bool harnessEnabled)
    {
        var tools = new IAgentTool[]
        {
            new StubNamedTool("file_list"),
            new StubMemoryTool("memory_search"),
            new StubMemoryTool("memory_get"),
            new StubHarnessTool("todo_write")
        };
        return new CompositeToolRouter(
            tools,
            new StubMcpRegistry([]),
            new AppSettings(),
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(harnessEnabled),
            RouterTestDependencies.CreateRunContextAccessor(harnessEnabled),
            RouterTestDependencies.CreateWorkspaceGuard());
    }

    private sealed class StubNamedTool(string name) : IAgentTool
    {
        public ToolDefinition Definition => new(name, name, new Dictionary<string, string>());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubMemoryTool(string name) : IAgentTool, ILongTermMemoryTool
    {
        public ToolDefinition Definition => new(name, name, new Dictionary<string, string>());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubHarnessTool(string name) : IAgentTool, IHarnessTool
    {
        public ToolDefinition Definition => new(name, name, new Dictionary<string, string>());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubMcpRegistry : IMcpRegistry
    {
        public StubMcpRegistry(IReadOnlyList<ToolDefinition> definitions) => Definitions = definitions.ToList();

        public List<ToolDefinition> Definitions { get; }

        public IReadOnlyList<McpServerStatus> GetStatuses() => Array.Empty<McpServerStatus>();

        public IReadOnlyList<ToolDefinition> ListToolDefinitions() => Definitions;

        public IReadOnlyList<McpCatalogEntry> ListCatalogEntries() =>
            Definitions.Select(definition =>
            {
                McpToolNameCodec.TryDecode(definition.Name, out var server, out var tool);
                return new McpCatalogEntry(server, tool, definition.Name, definition.Description, "{}");
            }).ToArray();

        public int CatalogVersion => 0;
        public int CatalogCount => ListCatalogEntries().Count;
        public int CatalogSchemaCharCount => ListCatalogEntries().Sum(entry => entry.Description.Length + entry.InputSchemaJson.Length);
        public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(string query, int topK, double minScore, string? serverName = null) =>
            McpSearchIndex.Search(ListCatalogEntries(), query, topK, minScore);

        public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ToolResult> InvokeAsync(
            string serverName,
            string toolName,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
