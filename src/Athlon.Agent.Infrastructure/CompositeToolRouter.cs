using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Core.Tools;
using Athlon.Agent.Infrastructure.Tools;

namespace Athlon.Agent.Infrastructure;

public sealed class CompositeToolRouter : IToolRouter
{
    private readonly McpDelegatingToolRouter _inner;

    public CompositeToolRouter(
        IEnumerable<IAgentTool> localTools,
        IMcpRegistry mcpRegistry,
        AppSettings settings,
        IActiveAgentSessionContext activeSessionContext,
        ISessionKnowledgeState sessionKnowledgeState,
        ISessionHarnessState sessionHarnessState,
        IAgentRunContextAccessor runContextAccessor,
        IDebugPhaseAccessor debugPhaseAccessor,
        IPlanPhaseAccessor planPhaseAccessor,
        WorkspaceGuard workspaceGuard,
        IBrowserWorkspaceState browserWorkspaceState,
        ITerminalWorkspaceState terminalWorkspaceState,
        IToolAvailabilityContextFactory? availabilityFactory = null,
        McpAdvertisementPolicy? advertisementPolicy = null,
        IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null,
        IAppLogger? logger = null)
    {
        var factory = availabilityFactory ?? new ToolAvailabilityContextFactory(
            activeSessionContext,
            sessionKnowledgeState,
            sessionHarnessState,
            runContextAccessor,
            debugPhaseAccessor,
            planPhaseAccessor,
            workspaceGuard,
            browserWorkspaceState,
            terminalWorkspaceState);
        var advertisement = advertisementPolicy
            ?? new McpAdvertisementPolicy(settings, mcpRegistry, activeSessionContext);
        _inner = new McpDelegatingToolRouter(
            static tools => tools,
            localTools,
            mcpRegistry,
            settings,
            factory,
            advertisement,
            runContextAccessor: runContextAccessor,
            activeSessionContext: activeSessionContext,
            runtimeDiagnosticEventSink: runtimeDiagnosticEventSink,
            logger: logger);
    }

    public IReadOnlyList<ToolDefinition> ListTools() => _inner.ListTools();

    public ToolDefinition? FindDefinition(string name) => _inner.FindDefinition(name);

    public bool IsParallelizable(string toolName) => _inner.IsParallelizable(toolName);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        _inner.InvokeAsync(invocation, cancellationToken);
}
