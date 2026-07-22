using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class PlanWorkflowSectionTests
{
    [Fact]
    public void Append_IncludesDetailedPlanGuidance_WhenPlanMode()
    {
        var builder = new StringBuilder();
        new PlanWorkflowSection().Append(builder, CreateContext(SessionAgentMode.Plan));

        var text = builder.ToString();
        Assert.Contains("Session Plan mode workflow:", text, StringComparison.Ordinal);
        Assert.Contains("create_plan", text, StringComparison.Ordinal);
        Assert.Contains("mermaid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update_plan", text, StringComparison.Ordinal);
        Assert.Contains("wait", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Append_Skips_WhenCodingMode()
    {
        var builder = new StringBuilder();
        new PlanWorkflowSection().Append(builder, CreateContext(SessionAgentMode.Coding));

        Assert.Equal(string.Empty, builder.ToString());
    }

    private static EnvironmentPromptContext CreateContext(SessionAgentMode mode) =>
        new()
        {
            Session = AgentSession.Create("plan-workflow-test"),
            WorkspaceRoot = @"C:\work\demo",
            Tools =
            [
                new ToolDefinition("create_plan", "Plan", ToolSchema.Object().Build()),
            ],
            SkillsDirectory = @"C:\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\skills", @"C:\app"),
            PromptSettings = new PromptSettings(),
            AgentMode = mode,
        };
}
