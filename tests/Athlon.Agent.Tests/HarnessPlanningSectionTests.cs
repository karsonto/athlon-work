using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure.Prompt;

namespace Athlon.Agent.Tests;

public sealed class HarnessPlanningSectionTests
{
    [Fact]
    public void Append_IncludesCodingPlanning_WhenCodingMode()
    {
        var builder = new StringBuilder();
        new HarnessPlanningSection().Append(builder, CreateContext(SessionAgentMode.Coding));

        var text = builder.ToString();
        Assert.Contains("Coding planning:", text, StringComparison.Ordinal);
        Assert.Contains("todo_write", text, StringComparison.Ordinal);
        Assert.Contains("in_progress", text, StringComparison.Ordinal);
        Assert.Contains("merge=false", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_Skips_WhenAgentMode()
    {
        var builder = new StringBuilder();
        new HarnessPlanningSection().Append(builder, CreateContext(SessionAgentMode.Agent));

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_Skips_WhenAskMode()
    {
        var builder = new StringBuilder();
        new HarnessPlanningSection().Append(builder, CreateContext(SessionAgentMode.Ask));

        Assert.Equal(string.Empty, builder.ToString());
    }

    private static EnvironmentPromptContext CreateContext(SessionAgentMode mode) =>
        new()
        {
            Session = AgentSession.Create("harness-planning-test"),
            WorkspaceRoot = @"C:\work\demo",
            WorkspaceName = "demo",
            IgnorePatterns = [".git"],
            Tools =
            [
                new ToolDefinition("todo_write", "Todos", ToolSchema.Object().Build()),
            ],
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(
                @"C:\Users\test\.athlon-agent\skills",
                @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings(),
            AgentMode = mode,
        };
}
