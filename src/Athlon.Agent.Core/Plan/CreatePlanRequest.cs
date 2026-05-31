namespace Athlon.Agent.Core.Plan;

public sealed record CreatePlanRequest(
    string Name,
    string Description,
    string ExpectedOutcome,
    IReadOnlyList<SubTaskInput> Subtasks);

public sealed record SubTaskInput(string Name, string? Description, string? ExpectedOutcome);
