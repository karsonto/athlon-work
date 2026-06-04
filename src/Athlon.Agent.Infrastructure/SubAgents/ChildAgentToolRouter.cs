using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class ChildAgentToolRouter(IEnumerable<IAgentTool> localTools, IMcpRegistry mcpRegistry) : IToolRouter
{
    private readonly ToolRouter _local = new(
        localTools.Where(tool => tool is not IExcludedFromChildAgentToolkit));

    public IReadOnlyList<ToolDefinition> ListTools()
    {
        var local = _local.ListTools();
        var mcp = mcpRegistry.ListToolDefinitions();
        return local.Concat(mcp).OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (McpToolNameCodec.TryDecode(invocation.ToolName, out var serverName, out var toolName))
        {
            return mcpRegistry.InvokeAsync(serverName, toolName, invocation.Arguments, cancellationToken);
        }

        return _local.InvokeAsync(invocation, cancellationToken);
    }
}
