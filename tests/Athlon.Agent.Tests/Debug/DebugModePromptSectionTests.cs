using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure.Prompt;

namespace Athlon.Agent.Tests.Debug;

public sealed class DebugModePromptSectionTests
{
    [Fact]
    public void Append_IncludesEvidenceGateAndHypothesisFormat()
    {
        var builder = new StringBuilder();
        new DebugModePromptSection().Append(builder, CreateContext(SessionAgentMode.Debug));
        var text = builder.ToString();
        Assert.Contains("never claim a root cause", text, StringComparison.Ordinal);
        Assert.Contains("- H1:", text, StringComparison.Ordinal);
        Assert.Contains("## Repro steps", text, StringComparison.Ordinal);
        Assert.Contains("Empty logs are not evidence", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_SkipsOutsideDebugMode()
    {
        var builder = new StringBuilder();
        new DebugModePromptSection().Append(builder, CreateContext(SessionAgentMode.Agent));
        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void AgentModeSection_RequiresLogHitsBeforeRootCause()
    {
        var builder = new StringBuilder();
        new AgentModeSection().Append(builder, CreateContext(SessionAgentMode.Debug));
        var text = builder.ToString();
        Assert.Contains("debug_read_logs", text, StringComparison.Ordinal);
        Assert.Contains("insufficient", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Coding workflow:", text, StringComparison.Ordinal);
    }

    private static EnvironmentPromptContext CreateContext(SessionAgentMode mode) =>
        new()
        {
            Session = AgentSession.Create("debug-prompt-test").WithWorkspace(@"C:\work\demo"),
            WorkspaceRoot = @"C:\work\demo",
            WorkspaceName = "demo",
            IgnorePatterns = [".git"],
            Tools =
            [
                new ToolDefinition("file_read", "Read", ToolSchema.Object().Build()),
                new ToolDefinition("debug_read_logs", "Logs", ToolSchema.Object().Build())
            ],
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(
                @"C:\Users\test\.athlon-agent\skills",
                @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings(),
            AgentMode = mode
        };
}
