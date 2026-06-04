namespace Athlon.Agent.Core.SubAgents;

public sealed class SubAgentExecutionScope : IDisposable
{
    private static readonly AsyncLocal<int> Depth = new();

    private readonly int _previous;

    private SubAgentExecutionScope()
    {
        _previous = Depth.Value;
        Depth.Value = _previous + 1;
    }

    public static int CurrentDepth => Depth.Value;

    public static IDisposable Enter() => new SubAgentExecutionScope();

    public void Dispose() => Depth.Value = _previous;
}
