namespace Athlon.Agent.Core.Plan;

public sealed class AgentPlan
{
    public AgentPlan(string name, string description, string expectedOutcome, IReadOnlyList<AgentSubTask> subtasks)
    {
        Name = name;
        Description = description;
        ExpectedOutcome = expectedOutcome;
        Subtasks = subtasks;
        CreatedAt = DateTimeOffset.Now;
    }

    public string Name { get; set; }
    public string Description { get; set; }
    public string ExpectedOutcome { get; set; }
    public IReadOnlyList<AgentSubTask> Subtasks { get; }
    public DateTimeOffset CreatedAt { get; }
}
