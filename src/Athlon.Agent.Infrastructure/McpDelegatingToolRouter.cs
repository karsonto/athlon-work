using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Core.Tools;

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
    IDebugPhaseAccessor debugPhaseAccessor,
    WorkspaceGuard workspaceGuard,
    IBrowserWorkspaceState browserWorkspaceState,
    ITerminalWorkspaceState terminalWorkspaceState,
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
    private readonly ConcurrentDictionary<string, bool> _autoSearchStickyBySession = new(StringComparer.Ordinal);

    private ToolRouter? _cachedLocalRouter;
    private string? _cachedLocalStamp;
    private readonly object _localRouterGate = new();

    private bool IsChatOnlyMode => !workspaceGuard.HasConfiguredWorkspace;

    private bool IsComputerUseMode => runContextAccessor.Current?.ComputerUseActive == true;

    private IEnumerable<IAgentTool> ActiveLocalTools => _allLocalTools.Where(IsToolEnabled);

    private bool IsToolEnabled(IAgentTool tool) =>
        ToolAvailabilityPolicy.IsEnabled(tool, BuildAvailabilityContext());

    private ToolAvailabilityContext BuildAvailabilityContext()
    {
        var sessionId = activeSessionContext.SessionId;
        var mode = ResolveSessionAgentMode();
        var debugPhase = mode == SessionAgentMode.Debug
            ? debugPhaseAccessor.GetPhase(sessionId)
            : null;
        return new ToolAvailabilityContext(
            ComputerUseActive: IsComputerUseMode,
            HasWorkspace: workspaceGuard.HasConfiguredWorkspace,
            WorkspaceKind: workspaceGuard.CurrentKind,
            Mode: mode,
            BrowserTabOpen: browserWorkspaceState.HasOpenBrowserTab,
            TerminalTabOpen: terminalWorkspaceState.HasOpenTerminalTab,
            KnowledgeEnabled: sessionKnowledgeState.ShouldExposeKnowledgeTool(sessionId),
            ActiveDebugPhase: debugPhase);
    }

    private SessionAgentMode ResolveSessionAgentMode()
    {
        if (sessionHarnessState.IsCodingModeForActiveRun(runContextAccessor))
        {
            return SessionAgentMode.Coding;
        }

        if (sessionHarnessState.IsAskModeForActiveRun(runContextAccessor))
        {
            return SessionAgentMode.Ask;
        }

        if (sessionHarnessState.IsPlanModeForActiveRun(runContextAccessor))
        {
            return SessionAgentMode.Plan;
        }

        if (sessionHarnessState.IsDebugModeForActiveRun(runContextAccessor))
        {
            return SessionAgentMode.Debug;
        }

        return SessionAgentMode.Agent;
    }

    private ToolRouter GetOrCreateLocalRouter()
    {
        var stamp = ComputeLocalStamp();
        lock (_localRouterGate)
        {
            if (_cachedLocalRouter is not null
                && string.Equals(_cachedLocalStamp, stamp, StringComparison.Ordinal))
            {
                return _cachedLocalRouter;
            }

            var router = new ToolRouter(ActiveLocalTools);
            _cachedLocalRouter = router;
            _cachedLocalStamp = stamp;
            return router;
        }
    }

    private string ComputeLocalStamp()
    {
        var sessionId = activeSessionContext.SessionId ?? string.Empty;
        var knowledge = sessionKnowledgeState.ShouldExposeKnowledgeTool(sessionId);
        var coding = sessionHarnessState.IsCodingModeForActiveRun(runContextAccessor);
        var ask = sessionHarnessState.IsAskModeForActiveRun(runContextAccessor);
        var plan = sessionHarnessState.IsPlanModeForActiveRun(runContextAccessor);
        var debug = sessionHarnessState.IsDebugModeForActiveRun(runContextAccessor);
        var debugPhase = debug ? debugPhaseAccessor.GetPhase(sessionId)?.ToString() ?? "none" : "off";
        var browser = browserWorkspaceState.HasOpenBrowserTab;
        var terminal = terminalWorkspaceState.HasOpenTerminalTab;
        var computerUse = IsComputerUseMode;
        return string.Join(
            '|',
            (int)workspaceGuard.CurrentKind,
            workspaceGuard.HasConfiguredWorkspace,
            sessionId,
            knowledge,
            coding,
            ask,
            plan,
            debug,
            debugPhase,
            browser,
            terminal,
            computerUse);
    }

    public IReadOnlyList<ToolDefinition> ListTools()
    {
        var local = GetOrCreateLocalRouter().ListTools();
        if (IsComputerUseMode || IsChatOnlyMode || IsDebugMcpBlocked())
        {
            return Canonicalize(local);
        }

        var useSearch = ShouldUseMcpSearch();
        if (!useSearch)
        {
            var mcp = mcpRegistry.ListToolDefinitions();
            return Canonicalize(local.Concat(mcp).ToArray());
        }

        var gateway = _searchGatewayTools.Value.Select(tool => tool.Definition);
        var tools = local.Concat(gateway).ToArray();
        _logger.Information(
            "MCP tool advertisement mode=search tools={ToolCount} catalog={CatalogCount} schemaChars={SchemaChars}",
            tools.Length,
            mcpRegistry.CatalogCount,
            mcpRegistry.CatalogSchemaCharCount);
        return Canonicalize(tools);
    }

    private IReadOnlyList<ToolDefinition> Canonicalize(IReadOnlyList<ToolDefinition> tools) =>
        Athlon.Agent.Core.Prompt.ToolOrderCanonicalizer.Apply(tools, settings.Prompt.ToolOrder);

    public ToolDefinition? FindDefinition(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var local = GetOrCreateLocalRouter().FindDefinition(name);
        if (local is not null)
        {
            return local;
        }

        if (IsComputerUseMode || IsChatOnlyMode || IsDebugMcpBlocked())
        {
            return null;
        }

        if (ShouldUseMcpSearch())
        {
            return _searchGatewayTools.Value
                .Select(tool => tool.Definition)
                .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        return mcpRegistry.ListToolDefinitions()
            .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsParallelizable(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        if (IsComputerUseMode)
        {
            return false;
        }

        var localRouter = GetOrCreateLocalRouter();
        if (localRouter.IsParallelizable(toolName))
        {
            return true;
        }

        if (IsDebugMcpBlocked() || !ShouldUseMcpSearch())
        {
            return false;
        }

        return _searchGatewayTools.Value.Any(tool =>
            string.Equals(tool.Definition.Name, toolName, StringComparison.OrdinalIgnoreCase)
            && tool is IParallelizableAgentTool);
    }

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (IsComputerUseMode)
        {
            var computerUseRouter = GetOrCreateLocalRouter();
            if (computerUseRouter.FindDefinition(invocation.ToolName) is null)
            {
                return Task.FromResult(ToolResult.Failure(
                    "Tool not available",
                    $"Tool '{invocation.ToolName}' is not available during a Computer Use turn."));
            }

            return computerUseRouter.InvokeAsync(invocation, cancellationToken);
        }

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

        if (IsDebugMcpBlocked()
            && (IsSearchGatewayTool(invocation.ToolName)
                || McpToolNameCodec.TryDecode(invocation.ToolName, out _, out _)))
        {
            return Task.FromResult(ToolResult.Failure(
                "Tool not available",
                "MCP tools are not available during this Debug phase. Use file/grep tools, then debug_read_logs after the user reproduces."));
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

            if (!invocation.SkipValidation)
            {
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
            }

            return mcpRegistry.InvokeAsync(serverName, toolName, invocation.Arguments, cancellationToken);
        }

        var localRouter = GetOrCreateLocalRouter();
        if (!invocation.SkipValidation)
        {
            var localDefinition = localRouter.FindDefinition(invocation.ToolName);
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
        }

        return localRouter.InvokeAsync(invocation, cancellationToken);
    }

    private bool IsDebugMcpBlocked()
    {
        if (ResolveSessionAgentMode() != SessionAgentMode.Debug)
        {
            return false;
        }

        var phase = debugPhaseAccessor.GetPhase(activeSessionContext.SessionId);
        return phase is null || phase.Value.BlocksMcp();
    }

    private bool ShouldUseMcpSearch()
    {
        var config = settings.McpSearch;
        if (!config.Enabled)
        {
            ClearStickyForActiveSession();
            return false;
        }

        if (string.Equals(config.Mode, "direct", StringComparison.OrdinalIgnoreCase))
        {
            ClearStickyForActiveSession();
            return false;
        }

        if (string.Equals(config.Mode, "search", StringComparison.OrdinalIgnoreCase))
        {
            ClearStickyForActiveSession();
            return mcpRegistry.CatalogCount > 0;
        }

        // auto
        var enterSearch = MeetsAutoEnterThreshold(config);
        var sessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return enterSearch;
        }

        if (_autoSearchStickyBySession.TryGetValue(sessionId, out var stuckSearch))
        {
            if (stuckSearch)
            {
                if (MeetsAutoExitThreshold(config))
                {
                    _autoSearchStickyBySession[sessionId] = false;
                    return false;
                }

                return true;
            }

            if (enterSearch)
            {
                _autoSearchStickyBySession[sessionId] = true;
                return true;
            }

            return false;
        }

        _autoSearchStickyBySession[sessionId] = enterSearch;
        return enterSearch;
    }

    private bool MeetsAutoEnterThreshold(McpSearchSettings config) =>
        mcpRegistry.CatalogCount >= config.AutoThresholdToolCount
        || mcpRegistry.CatalogSchemaCharCount >= config.AutoThresholdSchemaChars;

    private bool MeetsAutoExitThreshold(McpSearchSettings config)
    {
        var exitToolCount = Math.Max(0, config.AutoThresholdToolCount - Math.Max(0, config.AutoHysteresisToolCount));
        var exitSchemaChars = Math.Max(0, config.AutoThresholdSchemaChars - Math.Max(0, config.AutoHysteresisSchemaChars));
        return mcpRegistry.CatalogCount < exitToolCount
            && mcpRegistry.CatalogSchemaCharCount < exitSchemaChars;
    }

    private void ClearStickyForActiveSession()
    {
        var sessionId = activeSessionContext.SessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _autoSearchStickyBySession.TryRemove(sessionId, out _);
        }
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
