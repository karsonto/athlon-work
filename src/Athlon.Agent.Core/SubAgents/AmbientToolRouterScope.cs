namespace Athlon.Agent.Core.SubAgents;

[Obsolete("Use IAgentRunContextAccessor.Push with AgentRunContext instead.")]
public sealed class AmbientToolRouterScope : IDisposable
{
    private static readonly AsyncLocal<IToolRouter?> Current = new();

    private readonly IToolRouter? _previous;

    private AmbientToolRouterScope(IToolRouter router)
    {
        _previous = Current.Value;
        Current.Value = router;
    }

    public static IToolRouter? CurrentRouter => Current.Value;

    public static IDisposable Enter(IToolRouter router) => new AmbientToolRouterScope(router);

    public void Dispose() => Current.Value = _previous;
}
