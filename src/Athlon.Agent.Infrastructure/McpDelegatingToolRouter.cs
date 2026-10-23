using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Core.Tools;
using Athlon.Agent.Infrastructure.Tools;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Facade over local + MCP tool sources. Availability and MCP advertisement are delegated to
/// <see cref="IToolAvailabilityContextFactory"/> / <see cref="McpAdvertisementPolicy"/> / <see cref="IToolSource"/>.
/// </summary>
internal sealed class McpDelegatingToolRouter : IToolRouter
{
    private readonly AppSettings _settings;
    private readonly IToolAvailabilityContextFactory _availabilityFactory;
    private readonly McpAdvertisementPolicy _advertisement;
    private readonly LocalToolSource _local;
    private readonly McpCatalogToolSource _mcpCatalog;
    private readonly McpSearchGatewayToolSource _mcpSearch;
    private readonly IAppLogger _logger;
    private readonly IReadOnlyList<IToolSource> _sources;

    /// <summary>DI / production constructor — collaborators injected.</summary>
    public McpDelegatingToolRouter(
        Func<IEnumerable<IAgentTool>, IEnumerable<IAgentTool>> localToolFilter,
        IEnumerable<IAgentTool> allLocalTools,
        IMcpRegistry mcpRegistry,
        AppSettings settings,
        IToolAvailabilityContextFactory availabilityFactory,
        McpAdvertisementPolicy advertisementPolicy,
        Func<Task>? refreshMcpCatalogAsync = null,
        IAgentRunContextAccessor? runContextAccessor = null,
        IActiveAgentSessionContext? activeSessionContext = null,
        IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null,
        IAppLogger? logger = null)
    {
        _settings = settings;
        _availabilityFactory = availabilityFactory;
        _advertisement = advertisementPolicy;
        _logger = (logger ?? NullAppLogger.Instance).ForContext("McpDelegatingToolRouter");
        var filtered = localToolFilter(allLocalTools).ToArray();
        _local = new LocalToolSource(filtered, availabilityFactory);
        _mcpCatalog = new McpCatalogToolSource(
            mcpRegistry,
            runContextAccessor ?? new AgentRunContextAccessor(),
            activeSessionContext ?? new NullActiveSessionContext(),
            runtimeDiagnosticEventSink);
        _mcpSearch = new McpSearchGatewayToolSource(mcpRegistry, settings, refreshMcpCatalogAsync);
        _sources = [_local, _mcpCatalog, _mcpSearch];
    }

    /// <summary>
    /// Compatibility constructor for existing tests that pass session/workspace collaborators directly.
    /// </summary>
    public McpDelegatingToolRouter(
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
        IPlanPhaseAccessor? planPhaseAccessor = null,
        Func<Task>? refreshMcpCatalogAsync = null,
        IAppLogger? logger = null,
        IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null)
        : this(
            localToolFilter,
            allLocalTools,
            mcpRegistry,
            settings,
            new ToolAvailabilityContextFactory(
                activeSessionContext,
                sessionKnowledgeState,
                sessionHarnessState,
                runContextAccessor,
                debugPhaseAccessor,
                planPhaseAccessor,
                workspaceGuard,
                browserWorkspaceState,
                terminalWorkspaceState),
            new McpAdvertisementPolicy(settings, mcpRegistry, activeSessionContext),
            refreshMcpCatalogAsync,
            runContextAccessor,
            activeSessionContext,
            runtimeDiagnosticEventSink,
            logger)
    {
    }

    public IReadOnlyList<ToolDefinition> ListTools()
    {
        var availability = _availabilityFactory.Create();
        var mcpBlocked = _advertisement.IsMcpBlocked(availability);
        var preferSearch = !mcpBlocked && _advertisement.ShouldUseSearch();
        var tools = new List<ToolDefinition>();
        foreach (var source in _sources)
        {
            if (!source.IsActive(availability, preferSearch, mcpBlocked))
            {
                continue;
            }

            tools.AddRange(source.ListDefinitions(availability));
        }

        if (preferSearch)
        {
            _logger.Information(
                "MCP tool advertisement mode=search tools={ToolCount} catalog={CatalogCount} schemaChars={SchemaChars}",
                tools.Count,
                _mcpSearch.CatalogCount,
                _mcpSearch.CatalogSchemaCharCount);
        }

        return ToolOrderCanonicalizer.Apply(tools, _settings.Prompt.ToolOrder);
    }

    public ToolDefinition? FindDefinition(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var availability = _availabilityFactory.Create();
        var mcpBlocked = _advertisement.IsMcpBlocked(availability);
        var preferSearch = !mcpBlocked && _advertisement.ShouldUseSearch();
        foreach (var source in _sources)
        {
            if (!source.IsActive(availability, preferSearch, mcpBlocked))
            {
                continue;
            }

            var definition = source.FindDefinition(name, availability);
            if (definition is not null)
            {
                return definition;
            }
        }

        return null;
    }

    public bool IsParallelizable(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var availability = _availabilityFactory.Create();
        if (availability.ComputerUseActive)
        {
            return false;
        }

        var mcpBlocked = _advertisement.IsMcpBlocked(availability);
        var preferSearch = !mcpBlocked && _advertisement.ShouldUseSearch();
        foreach (var source in _sources)
        {
            if (!source.IsActive(availability, preferSearch, mcpBlocked))
            {
                continue;
            }

            if (source.IsParallelizable(toolName, availability))
            {
                return true;
            }
        }

        return false;
    }

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var availability = _availabilityFactory.Create();
        var mcpBlocked = _advertisement.IsMcpBlocked(availability);
        var preferSearch = !mcpBlocked && _advertisement.ShouldUseSearch();

        if (availability.ComputerUseActive)
        {
            if (_local.FindDefinition(invocation.ToolName, availability) is null)
            {
                return Task.FromResult(ToolResult.Failure(
                    "Tool not available",
                    $"Tool '{invocation.ToolName}' is not available during a Computer Use turn."));
            }

            return _local.InvokeAsync(invocation, cancellationToken);
        }

        if (_mcpSearch.Owns(invocation.ToolName))
        {
            if (mcpBlocked)
            {
                return Task.FromResult(ToolResult.Failure(
                    "Tool not available",
                    _advertisement.McpBlockedReason(availability) ?? "MCP tools are not available."));
            }

            if (!preferSearch)
            {
                return Task.FromResult(ToolResult.Failure(
                    "MCP gateway not advertised",
                    $"Tool {invocation.ToolName} is available only when MCP search mode is active."));
            }

            return _mcpSearch.InvokeAsync(invocation, cancellationToken);
        }

        if (_mcpCatalog.Owns(invocation.ToolName))
        {
            if (mcpBlocked)
            {
                return Task.FromResult(ToolResult.Failure(
                    "Tool not available",
                    _advertisement.McpBlockedReason(availability) ?? "MCP tools are not available."));
            }

            if (preferSearch)
            {
                return Task.FromResult(ToolResult.Failure(
                    "MCP tool not advertised",
                    $"Tool {invocation.ToolName} is not advertised in search mode. Use {McpSearchGatewayTools.SearchToolName} and {McpSearchGatewayTools.CallToolName}."));
            }

            return _mcpCatalog.InvokeAsync(invocation, cancellationToken);
        }

        if (!invocation.SkipValidation)
        {
            var localDefinition = _local.FindDefinition(invocation.ToolName, availability);
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

        return _local.InvokeAsync(invocation, cancellationToken);
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

    private sealed class NullActiveSessionContext : IActiveAgentSessionContext
    {
        public string? SessionId => null;
        public void SetSession(string? sessionId) { }
        public IDisposable Enter(string sessionId) => EmptyDisposable.Instance;

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
