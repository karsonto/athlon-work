using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class ChildAgentToolRouter(
    IEnumerable<IAgentTool> localTools,
    IMcpRegistry mcpRegistry,
    AppSettings settings,
    IActiveAgentSessionContext activeSessionContext,
    ISessionKnowledgeState sessionKnowledgeState,
    WorkspaceGuard workspaceGuard) : IToolRouter
{
    private readonly McpDelegatingToolRouter _inner = new(
        static tools => tools.Where(tool => tool is not IExcludedFromChildAgentToolkit),
        localTools,
        mcpRegistry,
        settings,
        activeSessionContext,
        sessionKnowledgeState,
        workspaceGuard);

    public IReadOnlyList<ToolDefinition> ListTools() => _inner.ListTools();

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        _inner.InvokeAsync(invocation, cancellationToken);
}
