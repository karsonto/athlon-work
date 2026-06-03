namespace Athlon.Agent.Core.Plan;

public sealed class AgentPlan
{
    public AgentPlan(
        string name,
        string description,
        string expectedOutcome,
        IReadOnlyList<AgentSubTask> subtasks,
        string overview,
        string? architecture = null,
        string? mermaid = null,
        string? testingStrategy = null,
        string? outOfScope = null,
        PlanPhase phase = PlanPhase.Draft)
    {
        Name = name;
        Description = description;
        ExpectedOutcome = expectedOutcome;
        Subtasks = subtasks;
        Overview = string.IsNullOrWhiteSpace(overview) ? description : overview;
        Architecture = architecture ?? string.Empty;
        Mermaid = mermaid ?? string.Empty;
        TestingStrategy = testingStrategy ?? string.Empty;
        OutOfScope = outOfScope ?? string.Empty;
        Phase = phase;
        CreatedAt = DateTimeOffset.Now;
    }

    public string Name { get; set; }
    public string Description { get; set; }
    public string ExpectedOutcome { get; set; }
    public string Overview { get; set; }
    public string Architecture { get; set; }
    public string Mermaid { get; set; }
    public string TestingStrategy { get; set; }
    public string OutOfScope { get; set; }
    public IReadOnlyList<AgentSubTask> Subtasks { get; }
    public PlanPhase Phase { get; set; }
    public DateTimeOffset CreatedAt { get; }

    public AgentPlan WithPhase(PlanPhase phase) =>
        new(
            Name,
            Description,
            ExpectedOutcome,
            Subtasks,
            Overview,
            Architecture,
            Mermaid,
            TestingStrategy,
            OutOfScope,
            phase);

    public AgentPlan WithSubtasks(IReadOnlyList<AgentSubTask> subtasks, PlanPhase? phase = null) =>
        new(
            Name,
            Description,
            ExpectedOutcome,
            subtasks,
            Overview,
            Architecture,
            Mermaid,
            TestingStrategy,
            OutOfScope,
            phase ?? Phase);
}
