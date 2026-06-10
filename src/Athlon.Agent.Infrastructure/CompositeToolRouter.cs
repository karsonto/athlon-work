using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class CompositeToolRouter(
    IEnumerable<IAgentTool> localTools,
    IMcpRegistry mcpRegistry,
    AppSettings settings) : IToolRouter
{
    private readonly McpDelegatingToolRouter _inner = new(
        static tools => tools,
        localTools,
        mcpRegistry,
        settings);

    public IReadOnlyList<ToolDefinition> ListTools() => _inner.ListTools();

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        _inner.InvokeAsync(invocation, cancellationToken);
}
