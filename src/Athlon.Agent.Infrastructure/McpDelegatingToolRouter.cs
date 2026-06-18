using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure;

internal sealed class McpDelegatingToolRouter(
    Func<IEnumerable<IAgentTool>, IEnumerable<IAgentTool>> localToolFilter,
    IEnumerable<IAgentTool> allLocalTools,
    IMcpRegistry mcpRegistry,
    AppSettings settings,
    IActiveAgentSessionContext activeSessionContext,
    ISessionKnowledgeState sessionKnowledgeState,
    Func<Task>? refreshMcpCatalogAsync = null,
    IAppLogger? logger = null) : IToolRouter
{
    private readonly IAppLogger _logger = (logger ?? NullAppLogger.Instance).ForContext("McpDelegatingToolRouter");
    private readonly IAgentTool[] _allLocalTools = localToolFilter(allLocalTools).ToArray();
    private readonly Lazy<IReadOnlyList<IAgentTool>> _searchGatewayTools = new(() =>
        McpSearchGatewayTools.Create(
            mcpRegistry,
            settings,
            refreshMcpCatalogAsync ?? (() => mcpRegistry.RefreshAsync(settings.McpServers, CancellationToken.None))));

    private IEnumerable<IAgentTool> ActiveLocalTools => _allLocalTools.Where(IsToolEnabled);

    private bool IsToolEnabled(IAgentTool tool)
    {
        if (!settings.Memory.Enabled && tool is ILongTermMemoryTool)
        {
            return false;
        }

        if (tool is IGlobalKnowledgeTool)
        {
            return sessionKnowledgeState.ShouldExposeKnowledgeTool(activeSessionContext.SessionId);
        }

        return true;
    }

    public IReadOnlyList<ToolDefinition> ListTools()
    {
        var local = new ToolRouter(ActiveLocalTools).ListTools();
        var useSearch = ShouldUseMcpSearch();
        if (!useSearch)
        {
            var mcp = mcpRegistry.ListToolDefinitions();
            return local.Concat(mcp).OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var gateway = _searchGatewayTools.Value.Select(tool => tool.Definition);
        var tools = local.Concat(gateway).OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _logger.Information(
            "MCP tool advertisement mode=search tools={ToolCount} catalog={CatalogCount} schemaChars={SchemaChars}",
            tools.Length,
            mcpRegistry.CatalogCount,
            mcpRegistry.CatalogSchemaCharCount);
        return tools;
    }

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (IsSearchGatewayTool(invocation.ToolName))
        {
            var gateway = _searchGatewayTools.Value.FirstOrDefault(
                tool => string.Equals(tool.Definition.Name, invocation.ToolName, StringComparison.OrdinalIgnoreCase));
            return gateway?.InvokeAsync(invocation, cancellationToken)
                ?? Task.FromResult(ToolResult.Failure("Tool not found", $"No gateway tool named '{invocation.ToolName}'."));
        }

        if (McpToolNameCodec.TryDecode(invocation.ToolName, out var serverName, out var toolName))
        {
            if (ShouldUseMcpSearch())
            {
                return Task.FromResult(ToolResult.Failure(
                    "MCP tool not advertised",
                    $"Tool {invocation.ToolName} is not advertised in search mode. Use {McpSearchGatewayTools.SearchToolName} and {McpSearchGatewayTools.CallToolName}."));
            }

            return mcpRegistry.InvokeAsync(serverName, toolName, invocation.Arguments, cancellationToken);
        }

        return new ToolRouter(ActiveLocalTools).InvokeAsync(invocation, cancellationToken);
    }

    private bool ShouldUseMcpSearch()
    {
        var config = settings.McpSearch;
        if (!config.Enabled)
        {
            return false;
        }

        if (string.Equals(config.Mode, "direct", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(config.Mode, "search", StringComparison.OrdinalIgnoreCase))
        {
            return mcpRegistry.CatalogCount > 0;
        }

        return mcpRegistry.CatalogCount >= config.AutoThresholdToolCount
            || mcpRegistry.CatalogSchemaCharCount >= config.AutoThresholdSchemaChars;
    }

    private sealed class NullAppLogger : IAppLogger
    {
        public static readonly NullAppLogger Instance = new();
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }

    private static bool IsSearchGatewayTool(string toolName) =>
        string.Equals(toolName, McpSearchGatewayTools.SearchToolName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, McpSearchGatewayTools.DescribeToolName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, McpSearchGatewayTools.CallToolName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, McpSearchGatewayTools.RefreshCatalogToolName, StringComparison.OrdinalIgnoreCase);
}
