namespace Athlon.Agent.Core.Teams;

/// <summary>Ambient TeamTask for Leader tool invocations during an orchestrated run.</summary>
public sealed class TeamTaskExecutionScope : IDisposable
{
    private static readonly AsyncLocal<TeamTaskExecutionScope?> Ambient = new();

    private readonly TeamTaskExecutionScope? _previous;

    private TeamTaskExecutionScope(TeamTask task)
    {
        _previous = Ambient.Value;
        Task = task;
        Ambient.Value = this;
    }

    public static TeamTaskExecutionScope? Current => Ambient.Value;

    public TeamTask Task { get; private set; }

    public static IDisposable Enter(TeamTask task) => new TeamTaskExecutionScope(task);

    public void ReplaceTask(TeamTask task) => Task = task;

    public void Dispose() => Ambient.Value = _previous;
}
