using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure;

public sealed class CompositeToolRouter(
    IEnumerable<IAgentTool> localTools,
    IMcpRegistry mcpRegistry,
    AppSettings settings) : IToolRouter
{
    private readonly IAgentTool[] _allLocalTools = localTools.ToArray();

    private IEnumerable<IAgentTool> ActiveLocalTools =>
        settings.Memory.Enabled
            ? _allLocalTools
            : _allLocalTools.Where(tool => tool is not ILongTermMemoryTool);

    public IReadOnlyList<ToolDefinition> ListTools()
    {
        var local = new ToolRouter(ActiveLocalTools).ListTools();
        var mcp = mcpRegistry.ListToolDefinitions();
        return local.Concat(mcp).OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (McpToolNameCodec.TryDecode(invocation.ToolName, out var serverName, out var toolName))
        {
            return mcpRegistry.InvokeAsync(serverName, toolName, invocation.Arguments, cancellationToken);
        }

        return new ToolRouter(ActiveLocalTools).InvokeAsync(invocation, cancellationToken);
    }
}
