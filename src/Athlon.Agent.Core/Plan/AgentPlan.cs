namespace Athlon.Agent.Core.Plan;

public sealed class AgentPlan
{
    public AgentPlan(
        string name,
        string description,
        string expectedOutcome,
        IReadOnlyList<AgentSubTask> subtasks,
        PlanPhase phase = PlanPhase.Draft)
    {
        Name = name;
        Description = description;
        ExpectedOutcome = expectedOutcome;
        Subtasks = subtasks;
        Phase = phase;
        CreatedAt = DateTimeOffset.Now;
    }

    public string Name { get; set; }
    public string Description { get; set; }
    public string ExpectedOutcome { get; set; }
    public IReadOnlyList<AgentSubTask> Subtasks { get; }
    public PlanPhase Phase { get; set; }
    public DateTimeOffset CreatedAt { get; }
}
