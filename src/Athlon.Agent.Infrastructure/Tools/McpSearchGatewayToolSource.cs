using Athlon.Agent.Core;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Infrastructure.Tools;

/// <summary>MCP search-gateway tools (search / describe / call / refresh).</summary>
public sealed class McpSearchGatewayToolSource(
    IMcpRegistry mcpRegistry,
    AppSettings settings,
    Func<Task>? refreshMcpCatalogAsync = null) : IToolSource
{
    private readonly Lazy<IReadOnlyList<IAgentTool>> _tools = new(() =>
        McpSearchGatewayTools.Create(
            mcpRegistry,
            settings,
            refreshMcpCatalogAsync ?? (() => mcpRegistry.RefreshAsync(settings.McpServers, CancellationToken.None))));

    public string Name => "mcp-search";

    public bool IsActive(ToolAvailabilityContext availability, bool preferMcpSearch, bool mcpBlocked) =>
        !mcpBlocked && preferMcpSearch;

    public bool Owns(string toolName) =>
        string.Equals(toolName, McpSearchGatewayTools.SearchToolName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, McpSearchGatewayTools.DescribeToolName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, McpSearchGatewayTools.CallToolName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, McpSearchGatewayTools.RefreshCatalogToolName, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ToolDefinition> ListDefinitions(ToolAvailabilityContext availability) =>
        _tools.Value.Select(tool => tool.Definition).ToArray();

    public ToolDefinition? FindDefinition(string name, ToolAvailabilityContext availability) =>
        _tools.Value
            .Select(tool => tool.Definition)
            .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool IsParallelizable(string toolName, ToolAvailabilityContext availability) =>
        _tools.Value.Any(tool =>
            string.Equals(tool.Definition.Name, toolName, StringComparison.OrdinalIgnoreCase)
            && tool is IParallelizableAgentTool);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        new ToolRouter(_tools.Value).InvokeAsync(invocation, cancellationToken);

    public int CatalogCount => mcpRegistry.CatalogCount;

    public int CatalogSchemaCharCount => mcpRegistry.CatalogSchemaCharCount;
}
