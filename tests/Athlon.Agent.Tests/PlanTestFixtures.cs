using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

internal static class PlanTestFixtures
{
    public const string ShortSummary = "Ship the MVP feature with tests and docs.";
    public const string PlanOutcome = "Feature is implemented, verified, and documented in plan.md.";

    public static string Overview(int minChars = 200) =>
        new string('O', minChars) + " Plan background, goals, and key decisions for the workspace.";

    public static SubTaskInput Subtask(
        string name,
        string? descriptionSuffix = null,
        params string[] files) =>
        new(
            name,
            $"Implement {name}: update types, wiring, and tests across the repo. {descriptionSuffix}",
            $"Verify {name} with automated tests and manual smoke checks in the app.",
            files.Length > 0 ? files : null);

    public static CreatePlanRequest SampleRequest(
        string name = "Sample plan",
        int subtaskCount = 2,
        string? overview = null) =>
        new(
            name,
            ShortSummary,
            PlanOutcome,
            overview ?? Overview(),
            Enumerable.Range(0, subtaskCount)
                .Select(index => Subtask($"Step {index + 1}", $"phase-{index + 1}", $"src/step{index + 1}.cs"))
                .ToArray());

    public static AgentPlan SampleAgentPlan(PlanPhase phase = PlanPhase.Draft) =>
        PlanValidation.ToPlan(SampleRequest(), phase);
}
