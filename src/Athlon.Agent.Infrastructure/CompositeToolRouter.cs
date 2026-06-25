using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure;

public sealed class CompositeToolRouter(
    IEnumerable<IAgentTool> localTools,
    IMcpRegistry mcpRegistry,
    AppSettings settings,
    IActiveAgentSessionContext activeSessionContext,
    ISessionKnowledgeState sessionKnowledgeState,
    ISessionHarnessState sessionHarnessState,
    IAgentRunContextAccessor runContextAccessor,
    WorkspaceGuard workspaceGuard) : IToolRouter
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
        workspaceGuard);

    public IReadOnlyList<ToolDefinition> ListTools() => _inner.ListTools();

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        _inner.InvokeAsync(invocation, cancellationToken);
}
