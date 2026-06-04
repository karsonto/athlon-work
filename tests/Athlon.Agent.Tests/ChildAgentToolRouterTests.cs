using Athlon.Agent.Core;
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
        var registry = new StubMcpRegistry([new ToolDefinition("mcp__srv__search", "mcp", new Dictionary<string, string>())]);

        var router = new ChildAgentToolRouter([subAgent, other], registry);
        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.DoesNotContain("call_assistant", names);
        Assert.Contains("file_list", names);
        Assert.Contains("mcp__srv__search", names);
    }

    [Fact]
    public async Task InvokeAsync_RoutesMcpThroughSharedRegistry()
    {
        var registry = new StubMcpRegistry([]);
        registry.Definitions.Add(new ToolDefinition("mcp__srv__ping", "ping", new Dictionary<string, string>()));
        var router = new ChildAgentToolRouter(Array.Empty<IAgentTool>(), registry);

        var result = await router.InvokeAsync(new ToolInvocation("mcp__srv__ping", new Dictionary<string, string>()));

        Assert.True(result.Succeeded);
        Assert.Equal("mcp:ping", result.Content);
    }

    private sealed class StubSubAgentTool : IAgentTool, IExcludedFromChildAgentToolkit
    {
        public ToolDefinition Definition => new("call_assistant", "sub", new Dictionary<string, string>());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubNamedTool(string name) : IAgentTool
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

        public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ToolResult> InvokeAsync(
            string serverName,
            string toolName,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok", $"mcp:{toolName}"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
