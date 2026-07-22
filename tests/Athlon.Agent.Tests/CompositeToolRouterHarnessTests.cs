using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class CompositeToolRouterHarnessTests
{
    [Fact]
    public void ListTools_WhenNotCoding_ExcludesHarnessTools_ButKeepsMemoryWithWorkspace()
    {
        var router = CreateRouter(SessionAgentMode.Agent);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("memory_search", names);
        Assert.Contains("memory_get", names);
        Assert.DoesNotContain("todo_write", names);
        Assert.Contains("file_list", names);
    }

    [Fact]
    public void ListTools_WhenCoding_IncludesHarnessAndMemoryTools()
    {
        var router = CreateRouter(SessionAgentMode.Coding);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("memory_search", names);
        Assert.Contains("memory_get", names);
        Assert.Contains("todo_write", names);
        Assert.Contains("file_list", names);
    }

    [Fact]
    public void ListTools_WhenNoWorkspace_ExcludesMemoryTools()
    {
        var router = CreateRouter(SessionAgentMode.Agent, includeWriteTools: false, includeSubAgentTools: false, workspaceConfigured: false);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.DoesNotContain("memory_search", names);
        Assert.DoesNotContain("memory_get", names);
        Assert.DoesNotContain("todo_write", names);
        Assert.DoesNotContain("file_list", names);
    }

    [Fact]
    public async Task InvokeAsync_WhenNotCoding_ReturnsNotFoundForHarnessTools_ButResolvesMemory()
    {
        var router = CreateRouter(SessionAgentMode.Agent);

        var searchResult = await router.InvokeAsync(new ToolInvocation("memory_search", new Dictionary<string, string> { ["query"] = "test" }));
        var todoResult = await router.InvokeAsync(new ToolInvocation("todo_write", new Dictionary<string, string>
        {
            ["todos"] = """[{"id":"1","content":"x","status":"pending"}]""",
            ["merge"] = "false"
        }));

        Assert.True(searchResult.Succeeded);
        Assert.False(todoResult.Succeeded);
    }

    [Fact]
    public async Task InvokeAsync_WhenCoding_ResolvesMemoryTools()
    {
        var router = CreateRouter(SessionAgentMode.Coding);

        var result = await router.InvokeAsync(new ToolInvocation("memory_search", new Dictionary<string, string> { ["query"] = "nonexistent-xyz-123" }));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ListTools_WhenAskMode_ExcludesWriteAndExecuteTools_ButKeepsMemory()
    {
        var router = CreateRouter(SessionAgentMode.Ask, includeWriteTools: true, includeSubAgentTools: true);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("file_list", names);
        Assert.Contains("memory_search", names);
        Assert.Contains("memory_get", names);
        Assert.DoesNotContain("file_write", names);
        Assert.DoesNotContain("file_edit", names);
        Assert.DoesNotContain("apply_patch", names);
        Assert.DoesNotContain("execute_command", names);
        Assert.DoesNotContain("todo_write", names);
        Assert.DoesNotContain("create_plan", names);
        Assert.DoesNotContain("sessions_spawn", names);
    }

    [Fact]
    public void ListTools_WhenPlanMode_IncludesPlanTools_ExcludesWritesAndTodo()
    {
        var router = CreateRouter(SessionAgentMode.Plan, includeWriteTools: true, includeSubAgentTools: true);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("create_plan", names);
        Assert.Contains("update_plan", names);
        Assert.Contains("file_list", names);
        Assert.DoesNotContain("todo_write", names);
        Assert.DoesNotContain("file_write", names);
        Assert.DoesNotContain("execute_command", names);
        Assert.DoesNotContain("sessions_spawn", names);
    }

    [Fact]
    public void ListTools_WhenCoding_ExcludesPlanTools()
    {
        var router = CreateRouter(SessionAgentMode.Coding);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("todo_write", names);
        Assert.DoesNotContain("create_plan", names);
        Assert.DoesNotContain("update_plan", names);
    }

    [Fact]
    public void ListTools_WhenAgentMode_IncludesSubAgentTools()
    {
        var router = CreateRouter(SessionAgentMode.Agent, includeWriteTools: false, includeSubAgentTools: true);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Contains("sessions_spawn", names);
    }

    [Fact]
    public async Task InvokeAsync_WhenAskMode_ReturnsNotFoundForBlockedTools()
    {
        var router = CreateRouter(SessionAgentMode.Ask, includeWriteTools: true, includeSubAgentTools: true);
        var writeResult = await router.InvokeAsync(new ToolInvocation("file_write", new Dictionary<string, string>()));
        var commandResult = await router.InvokeAsync(new ToolInvocation("execute_command", new Dictionary<string, string>()));
        var spawnResult = await router.InvokeAsync(new ToolInvocation("sessions_spawn", new Dictionary<string, string>()));

        Assert.False(writeResult.Succeeded);
        Assert.False(commandResult.Succeeded);
        Assert.False(spawnResult.Succeeded);
    }

    private static CompositeToolRouter CreateRouter(SessionAgentMode mode) =>
        CreateRouter(mode, includeWriteTools: false, includeSubAgentTools: false, workspaceConfigured: true);

    private static CompositeToolRouter CreateRouter(
        SessionAgentMode mode,
        bool includeWriteTools,
        bool includeSubAgentTools = false,
        bool workspaceConfigured = true)
    {
        var tools = new List<IAgentTool>
        {
            new StubNamedTool("file_list"),
            new StubMemoryTool("memory_search"),
            new StubMemoryTool("memory_get"),
            new StubHarnessTool("todo_write"),
            new StubPlanTool("create_plan"),
            new StubPlanTool("update_plan"),
        };

        if (includeWriteTools)
        {
            tools.Add(new StubNamedTool("file_write"));
            tools.Add(new StubNamedTool("file_edit"));
            tools.Add(new StubNamedTool("apply_patch"));
            tools.Add(new StubNamedTool("execute_command"));
        }

        if (includeSubAgentTools)
        {
            tools.Add(new StubSubAgentTool("sessions_spawn"));
        }

        return new CompositeToolRouter(
            tools,
            new StubMcpRegistry([]),
            new AppSettings(),
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(mode),
            RouterTestDependencies.CreateRunContextAccessor(mode),
            RouterTestDependencies.CreateWorkspaceGuard(configured: workspaceConfigured));
    }

    private sealed class StubNamedTool(string name) : IAgentTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubMemoryTool(string name) : IAgentTool, ILongTermMemoryTool
    {
        public ToolDefinition Definition => new(
            name,
            name,
            name == "memory_search"
                ? ToolSchema.Object().String("query", "query", required: true).Build()
                : ToolSchema.Object().AllowAdditionalProperties().Build());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubHarnessTool(string name) : IAgentTool, IHarnessTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubPlanTool(string name) : IAgentTool, IPlanTool
    {
        public ToolDefinition Definition => new(name, name, ToolSchema.Object().Build());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubSubAgentTool(string name) : IAgentTool, ISubAgentTool, IExcludedFromChildAgentToolkit
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
        public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(string query, int topK, double minScore, string? serverName = null) =>
            McpSearchIndex.Search(ListCatalogEntries(), query, topK, minScore);

        public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ToolResult> InvokeAsync(
            string serverName,
            string toolName,
            ToolCallArguments arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
