using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class CompositeToolRouterMemoryTests
{
    [Fact]
    public void MemorySettings_DefaultsToDisabled()
    {
        var settings = new MemorySettings();

        Assert.False(settings.Enabled);
    }

    [Fact]
    public void ListTools_WhenMemoryDisabled_ExcludesMemoryTools()
    {
        var settings = new AppSettings { Memory = new MemorySettings { Enabled = false } };
        var router = CreateRouter(settings);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.DoesNotContain("memory_search", names);
        Assert.DoesNotContain("memory_get", names);
        Assert.Contains("file_list", names);
    }

    [Fact]
    public void ListTools_WhenMemoryEnabled_IncludesMemoryTools()
    {
        var settings = new AppSettings { Memory = new MemorySettings { Enabled = true } };
        var router = CreateRouter(settings);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("memory_search", names);
        Assert.Contains("memory_get", names);
        Assert.Contains("file_list", names);
    }

    [Fact]
    public async Task InvokeAsync_WhenMemoryDisabled_ReturnsNotFoundForMemoryTools()
    {
        var settings = new AppSettings { Memory = new MemorySettings { Enabled = false } };
        var router = CreateRouter(settings);

        var searchResult = await router.InvokeAsync(new ToolInvocation("memory_search", new Dictionary<string, string> { ["query"] = "test" }));
        var getResult = await router.InvokeAsync(new ToolInvocation("memory_get", new Dictionary<string, string>
        {
            ["path"] = "MEMORY.md",
            ["start_line"] = "1",
            ["end_line"] = "5"
        }));

        Assert.False(searchResult.Succeeded);
        Assert.Contains("memory_search", searchResult.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(getResult.Succeeded);
        Assert.Contains("memory_get", getResult.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_WhenMemoryEnabled_ResolvesMemoryTools()
    {
        var settings = new AppSettings { Memory = new MemorySettings { Enabled = true } };
        var router = CreateRouter(settings);

        var result = await router.InvokeAsync(new ToolInvocation("memory_search", new Dictionary<string, string> { ["query"] = "nonexistent-xyz-123" }));

        Assert.True(result.Succeeded);
    }

    private static CompositeToolRouter CreateRouter(AppSettings settings)
    {
        var tools = new IAgentTool[]
        {
            new StubNamedTool("file_list"),
            new StubMemoryTool("memory_search"),
            new StubMemoryTool("memory_get")
        };
        return new CompositeToolRouter(
            tools,
            new StubMcpRegistry([]),
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState());
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
