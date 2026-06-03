using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

public sealed class PlanMarkdownFormatterTests
{
    [Fact]
    public void ToMarkdown_Detailed_IncludesMermaidAndAcceptanceLabels()
    {
        var plan = PlanValidation.ToPlan(
            new CreatePlanRequest(
                "Demo",
                "One line summary",
                "Done when tests pass",
                PlanTestFixtures.Overview(),
                [PlanTestFixtures.Subtask("Step", files: "src/Foo.cs")],
                Mermaid: "flowchart LR\n  A --> B"));

        var markdown = PlanMarkdownFormatter.ToMarkdown(plan, detailed: true);

        Assert.Contains("## Overview", markdown, StringComparison.Ordinal);
        Assert.Contains("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Contains("flowchart LR", markdown, StringComparison.Ordinal);
        Assert.Contains("**Acceptance:**", markdown, StringComparison.Ordinal);
        Assert.Contains("`src/Foo.cs`", markdown, StringComparison.Ordinal);
    }
}
