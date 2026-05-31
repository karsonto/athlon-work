namespace Athlon.Agent.Core.Plan;

public sealed class AgentSubTask
{
    public AgentSubTask(string name, string description, string expectedOutcome)
    {
        Name = name;
        Description = description;
        ExpectedOutcome = expectedOutcome;
        CreatedAt = DateTimeOffset.Now;
    }

    public string Name { get; }
    public string Description { get; }
    public string ExpectedOutcome { get; }
    public SubTaskState State { get; set; } = SubTaskState.Todo;
    public string? Outcome { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public void Finish(string outcome)
    {
        State = SubTaskState.Done;
        Outcome = outcome;
        FinishedAt = DateTimeOffset.Now;
    }
}
