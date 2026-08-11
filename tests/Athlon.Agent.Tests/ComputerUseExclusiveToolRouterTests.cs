using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class ComputerUseExclusiveToolRouterTests
{
    [Theory]
    [InlineData("direct")]
    [InlineData("search")]
    public void ListTools_WhenComputerUseActive_ReturnsOnlyComputerUseTools(string mcpMode)
    {
        var router = CreateRouter(mcpMode);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();

        Assert.Equal(["computer_interact", "computer_observe", "computer_wait"], names);
        Assert.DoesNotContain("file_list", names);
        Assert.DoesNotContain(names, name => name.StartsWith("mcp_", StringComparison.Ordinal));
        Assert.Null(router.FindDefinition("file_list"));
        Assert.Null(router.FindDefinition(McpToolNameCodec.Encode("server", "ping")));
        Assert.NotNull(router.FindDefinition("computer_observe"));
        Assert.False(router.IsParallelizable("computer_observe"));
    }

    [Fact]
    public async Task InvokeAsync_WhenComputerUseActive_RejectsNativeAndMcpTools()
    {
        var router = CreateRouter();

        var native = await router.InvokeAsync(
            new ToolInvocation("file_list", ToolCallArguments.Empty));
        var mcp = await router.InvokeAsync(
            new ToolInvocation(
                McpToolNameCodec.Encode("server", "ping"),
                ToolCallArguments.Empty));

        Assert.False(native.Succeeded);
        Assert.False(mcp.Succeeded);
        Assert.Contains("not available", native.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not available", mcp.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_WhenComputerUseActive_AllowsComputerUseTool()
    {
        var router = CreateRouter();

        var result = await router.InvokeAsync(
            new ToolInvocation("computer_observe", ToolCallArguments.Empty));

        Assert.True(result.Succeeded);
    }

    private static McpDelegatingToolRouter CreateRouter(string mcpMode = "search")
    {
        IAgentTool[] tools =
        [
            new StubComputerUseTool("computer_observe"),
            new StubComputerUseTool("computer_interact"),
            new StubComputerUseTool("computer_wait"),
            new StubTool("file_list")
        ];
        var catalog = new[]
        {
            new McpCatalogEntry(
                "server",
                "ping",
                McpToolNameCodec.Encode("server", "ping"),
                "ping",
                "{}")
        };

        return new McpDelegatingToolRouter(
            static local => local,
            tools,
            new TestMcpRegistry(catalog),
            new AppSettings
            {
                McpSearch = new McpSearchSettings
                {
                    Enabled = true,
                    Mode = mcpMode
                }
            },
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            RouterTestDependencies.CreateRunContextAccessor(computerUseActive: true),
            RouterTestDependencies.CreateWorkspaceGuard(),
            RouterTestDependencies.CreateBrowserWorkspaceState());
    }

    private sealed class StubComputerUseTool(string name) : IAgentTool, IComputerUseTool
    {
        public ToolDefinition Definition { get; } =
            new(name, name, ToolSchema.Object().Build(), Source: "computer-use");

        public Task<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubTool(string name) : IAgentTool
    {
        public ToolDefinition Definition { get; } =
            new(name, name, ToolSchema.Object().Build());

        public Task<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
