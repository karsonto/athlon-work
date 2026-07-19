using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.SubAgents;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class ChildAgentToolRouterTests
{
    [Fact]
    public void ListTools_ExcludesSubAgentTool_IncludesOtherLocalTools()
    {
        var subAgent = new StubSubAgentTool();
        var other = new StubNamedTool("file_list");
        var registry = new StubMcpRegistry([new ToolDefinition("mcp__srv__search", "mcp", ToolSchema.Object().Build())]);

        var settings = new AppSettings();
        var router = new ChildAgentToolRouter(
            [subAgent, other],
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard());
        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.DoesNotContain("sessions_spawn", names);
        Assert.Contains("file_list", names);
        Assert.Contains("mcp__srv__search", names);
    }

    [Fact]
    public void ListTools_WhenWorkspaceConfigured_IncludesMemoryTools()
    {
        var memorySearch = new StubMemoryTool("memory_search");
        var other = new StubNamedTool("file_list");
        var registry = new StubMcpRegistry([]);
        var settings = new AppSettings();

        var router = new ChildAgentToolRouter(
            [memorySearch, other],
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard());
        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("memory_search", names);
        Assert.Contains("file_list", names);
    }

    [Fact]
    public void ListTools_WhenNoWorkspace_ExcludesMemoryTools()
    {
        var memorySearch = new StubMemoryTool("memory_search");
        var other = new StubNamedTool("file_list");
        var registry = new StubMcpRegistry([]);
        var settings = new AppSettings();

        var router = new ChildAgentToolRouter(
            [memorySearch, other],
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard(configured: false));
        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.DoesNotContain("memory_search", names);
        // Chat-only mode only exposes knowledge tools; file_list is also hidden.
        Assert.DoesNotContain("file_list", names);
    }

    [Fact]
    public async Task InvokeAsync_RoutesMcpThroughSharedRegistry()
    {
        var registry = new StubMcpRegistry([]);
        registry.Definitions.Add(new ToolDefinition("mcp__srv__ping", "ping", ToolSchema.Object().Build()));
        var settings = new AppSettings();
        var router = new ChildAgentToolRouter(
            Array.Empty<IAgentTool>(),
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard());

        var result = await router.InvokeAsync(new ToolInvocation("mcp__srv__ping", new Dictionary<string, string>()));

        Assert.True(result.Succeeded);
        Assert.Equal("mcp:ping", result.Content);
    }

    private sealed class StubSubAgentTool : IAgentTool, IExcludedFromChildAgentToolkit
    {
        public ToolDefinition Definition => new("sessions_spawn", "sub", ToolSchema.Object().Build());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubNamedTool(string name) : IAgentTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubMemoryTool(string name) : IAgentTool, ILongTermMemoryTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());
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
        public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(string query, int topK, double minScore, string? serverName = null)
        {
            var catalog = string.IsNullOrWhiteSpace(serverName)
                ? ListCatalogEntries()
                : ListCatalogEntries().Where(entry => string.Equals(entry.ServerName, serverName, StringComparison.OrdinalIgnoreCase)).ToArray();
            return McpSearchIndex.Search(catalog, query, topK, minScore);
        }

        public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ToolResult> InvokeAsync(
            string serverName,
            string toolName,
            ToolCallArguments arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok", $"mcp:{toolName}"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
