namespace Athlon.Agent.Core.Plan;

public sealed record CreatePlanRequest(
    string Name,
    string Description,
    string ExpectedOutcome,
    string Overview,
    IReadOnlyList<SubTaskInput> Subtasks,
    string? Architecture = null,
    string? Mermaid = null,
    string? TestingStrategy = null,
    string? OutOfScope = null);

public sealed record SubTaskInput(
    string Name,
    string? Description,
    string? ExpectedOutcome,
    IReadOnlyList<string>? Files = null);
