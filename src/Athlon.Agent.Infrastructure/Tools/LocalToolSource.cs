using Athlon.Agent.Core;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Infrastructure.Tools;

/// <summary>Filtered local tools with a stamp-invalidated <see cref="ToolRouter"/> cache.</summary>
public sealed class LocalToolSource(
    IReadOnlyList<IAgentTool> allLocalTools,
    IToolAvailabilityContextFactory availabilityFactory,
    IEnumerable<IToolAvailabilityRule>? availabilityRules = null) : IToolSource
{
    private readonly IToolAvailabilityRule[] _rules =
        (availabilityRules ?? ToolAvailabilityPolicy.DefaultRules).ToArray();
    private readonly object _gate = new();
    private ToolRouter? _cachedRouter;
    private string? _cachedStamp;

    public string Name => "local";

    public bool IsActive(ToolAvailabilityContext availability, bool preferMcpSearch, bool mcpBlocked) => true;

    public bool Owns(string toolName)
    {
        var router = GetOrCreateRouter();
        return router.FindDefinition(toolName) is not null;
    }

    public IReadOnlyList<ToolDefinition> ListDefinitions(ToolAvailabilityContext availability) =>
        GetOrCreateRouter().ListTools();

    public ToolDefinition? FindDefinition(string name, ToolAvailabilityContext availability) =>
        GetOrCreateRouter().FindDefinition(name);

    public bool IsParallelizable(string toolName, ToolAvailabilityContext availability) =>
        GetOrCreateRouter().IsParallelizable(toolName);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        GetOrCreateRouter().InvokeAsync(invocation, cancellationToken);

    private ToolRouter GetOrCreateRouter()
    {
        var stamp = availabilityFactory.ComputeLocalStamp();
        lock (_gate)
        {
            if (_cachedRouter is not null
                && string.Equals(_cachedStamp, stamp, StringComparison.Ordinal))
            {
                return _cachedRouter;
            }

            var ctx = availabilityFactory.Create();
            var enabled = allLocalTools
                .Where(tool => ToolAvailabilityPolicy.IsEnabled(tool, ctx, _rules))
                .ToArray();
            var router = new ToolRouter(enabled);
            _cachedRouter = router;
            _cachedStamp = stamp;
            return router;
        }
    }
}
