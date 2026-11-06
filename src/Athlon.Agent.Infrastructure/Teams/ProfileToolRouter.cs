using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Core.Tools;
using Athlon.Agent.Infrastructure.Tools;

namespace Athlon.Agent.Infrastructure.Teams;

/// <summary>
/// Per-profile tool router for Team members. Does not replace <see cref="SubAgents.ChildAgentToolRouter"/>.
/// </summary>
public sealed class ProfileToolRouter : IToolRouter
{
    private readonly McpDelegatingToolRouter _inner;

    public ProfileToolRouter(
        IReadOnlyList<string> allowList,
        bool isLeader,
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

        var allowed = allowList;
        _inner = new McpDelegatingToolRouter(
            tools => FilterTools(tools, allowed, isLeader),
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

    private static IEnumerable<IAgentTool> FilterTools(
        IEnumerable<IAgentTool> tools,
        IReadOnlyList<string> allowList,
        bool isLeader)
    {
        foreach (var tool in tools)
        {
            // Team Leader orchestration tools are IExcludedFromChildAgentToolkit so Workers /
            // sub-agents never inherit them — but the Leader itself must still receive them.
            if (tool is IExcludedFromChildAgentToolkit
                && !(isLeader && tool is ITeamLeaderTool))
            {
                continue;
            }

            if (tool is ITeamLeaderTool && !isLeader)
            {
                continue;
            }

            var name = tool.Definition.Name;
            if (!TeamToolDefaults.MatchesAllowList(name, allowList))
            {
                continue;
            }

            yield return tool;
        }
    }
}
