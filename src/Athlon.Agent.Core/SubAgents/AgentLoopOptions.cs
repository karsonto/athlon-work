namespace Athlon.Agent.Core.SubAgents;

public sealed class AgentLoopOptions
{
    public int? MaxModelToolRounds { get; init; }
}

[Obsolete("Use IAgentRunContextAccessor.Push with AgentRunContext instead.")]
public sealed class AgentLoopOptionsScope : IDisposable
{
    private static readonly AsyncLocal<AgentLoopOptions?> Ambient = new();

    private readonly AgentLoopOptions? _previous;

    private AgentLoopOptionsScope(AgentLoopOptions options)
    {
        _previous = Ambient.Value;
        Ambient.Value = options;
    }

    public static AgentLoopOptions? Current => Ambient.Value;

    public static IDisposable Enter(AgentLoopOptions options) => new AgentLoopOptionsScope(options);

    public void Dispose() => Ambient.Value = _previous;
}
