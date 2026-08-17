using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Terminal;

namespace Athlon.Agent.Infrastructure;

public sealed class CompositeToolRouter(
    IEnumerable<IAgentTool> localTools,
    IMcpRegistry mcpRegistry,
    AppSettings settings,
    IActiveAgentSessionContext activeSessionContext,
    ISessionKnowledgeState sessionKnowledgeState,
    ISessionHarnessState sessionHarnessState,
    IAgentRunContextAccessor runContextAccessor,
    IDebugPhaseAccessor debugPhaseAccessor,
    WorkspaceGuard workspaceGuard,
    IBrowserWorkspaceState browserWorkspaceState,
    ITerminalWorkspaceState terminalWorkspaceState) : IToolRouter
{
    private readonly McpDelegatingToolRouter _inner = new(
        static tools => tools,
        localTools,
        mcpRegistry,
        settings,
        activeSessionContext,
        sessionKnowledgeState,
        sessionHarnessState,
        runContextAccessor,
        debugPhaseAccessor,
        workspaceGuard,
        browserWorkspaceState,
        terminalWorkspaceState);

    public IReadOnlyList<ToolDefinition> ListTools() => _inner.ListTools();

    public ToolDefinition? FindDefinition(string name) => _inner.FindDefinition(name);

    public bool IsParallelizable(string toolName) => _inner.IsParallelizable(toolName);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        _inner.InvokeAsync(invocation, cancellationToken);
}
