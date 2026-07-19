using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure;

internal sealed class McpDelegatingToolRouter(
    Func<IEnumerable<IAgentTool>, IEnumerable<IAgentTool>> localToolFilter,
    IEnumerable<IAgentTool> allLocalTools,
    IMcpRegistry mcpRegistry,
    AppSettings settings,
    IActiveAgentSessionContext activeSessionContext,
    ISessionKnowledgeState sessionKnowledgeState,
    ISessionHarnessState sessionHarnessState,
    IAgentRunContextAccessor runContextAccessor,
    WorkspaceGuard workspaceGuard,
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

    private bool IsChatOnlyMode => !workspaceGuard.HasConfiguredWorkspace;

    private IEnumerable<IAgentTool> ActiveLocalTools => _allLocalTools.Where(IsToolEnabled);

    private bool IsToolEnabled(IAgentTool tool)
    {
        if (IsChatOnlyMode)
        {
            return tool is IGlobalKnowledgeTool
                && sessionKnowledgeState.ShouldExposeKnowledgeTool(activeSessionContext.SessionId);
        }

        if (tool is ILocalWorkspaceTool && workspaceGuard.CurrentKind == WorkspaceKind.Ssh)
        {
            return false;
        }

        if (tool is IRemoteWorkspaceTool && workspaceGuard.CurrentKind != WorkspaceKind.Ssh)
        {
            return false;
        }

        if (tool is IHarnessTool && !sessionHarnessState.IsCodingModeForActiveRun(runContextAccessor))
        {
            return false;
        }

        if (tool is ILongTermMemoryTool && !workspaceGuard.HasConfiguredWorkspace)
        {
            return false;
        }

        if (sessionHarnessState.IsAskModeForActiveRun(runContextAccessor)
            && (tool.Definition.Name is "file_write" or "file_edit" or "apply_patch" or "execute_command"
                || tool is ISubAgentTool))
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
        if (IsChatOnlyMode)
        {
            return local;
        }

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
        if (IsChatOnlyMode)
        {
            if (IsSearchGatewayTool(invocation.ToolName)
                || McpToolNameCodec.TryDecode(invocation.ToolName, out _, out _))
            {
                return Task.FromResult(ToolResult.Failure(
                    "Tool not available",
                    "This tool is not available without a configured workspace."));
            }
        }

        if (IsSearchGatewayTool(invocation.ToolName))
        {
            if (!ShouldUseMcpSearch())
            {
                return Task.FromResult(ToolResult.Failure(
                    "MCP gateway not advertised",
                    $"Tool {invocation.ToolName} is available only when MCP search mode is active."));
            }

            return new ToolRouter(_searchGatewayTools.Value).InvokeAsync(invocation, cancellationToken);
        }

        if (McpToolNameCodec.TryDecode(invocation.ToolName, out var serverName, out var toolName))
        {
            if (ShouldUseMcpSearch())
            {
                return Task.FromResult(ToolResult.Failure(
                    "MCP tool not advertised",
                    $"Tool {invocation.ToolName} is not advertised in search mode. Use {McpSearchGatewayTools.SearchToolName} and {McpSearchGatewayTools.CallToolName}."));
            }

            var mcpDefinition = mcpRegistry.ListToolDefinitions()
                .FirstOrDefault(tool => string.Equals(tool.Name, invocation.ToolName, StringComparison.OrdinalIgnoreCase));
            if (mcpDefinition is not null)
            {
                var validationError = ToolInvocationValidator.Validate(mcpDefinition.ParametersSchema, invocation.Arguments);
                if (validationError is not null)
                {
                    return Task.FromResult(ToolInvocationErrors.Failure("Invalid tool arguments", validationError));
                }

                var blocked = ToolInvocationPolicyEnforcer.TryBlockInvocation(
                    mcpDefinition,
                    invocation.ApprovalDecision);
                if (blocked is not null)
                {
                    return Task.FromResult(blocked);
                }
            }

            return mcpRegistry.InvokeAsync(serverName, toolName, invocation.Arguments, cancellationToken);
        }

        var localRouter = new ToolRouter(ActiveLocalTools);
        var localDefinition = localRouter.ListTools()
            .FirstOrDefault(tool => string.Equals(tool.Name, invocation.ToolName, StringComparison.OrdinalIgnoreCase));
        if (localDefinition is not null)
        {
            var validationError = ToolInvocationValidator.Validate(localDefinition.ParametersSchema, invocation.Arguments);
            if (validationError is not null)
            {
                return Task.FromResult(ToolInvocationErrors.Failure("Invalid tool arguments", validationError));
            }

            var blocked = ToolInvocationPolicyEnforcer.TryBlockInvocation(
                localDefinition,
                invocation.ApprovalDecision);
            if (blocked is not null)
            {
                return Task.FromResult(blocked);
            }
        }

        return localRouter.InvokeAsync(invocation, cancellationToken);
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
