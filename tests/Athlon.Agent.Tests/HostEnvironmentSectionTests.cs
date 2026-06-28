using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class HostEnvironmentSectionTests
{
    [Fact]
    public void Append_ProducesStableOutput_WithoutClockTimestamp()
    {
        var context = CreateContext();
        var first = Render(context);
        var second = Render(context);

        Assert.Equal(first, second);
        Assert.Contains("tz=UTC+8", first, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", first);
    }

    private static string Render(EnvironmentPromptContext context)
    {
        var builder = new StringBuilder();
        new HostEnvironmentSection().Append(builder, context);
        return builder.ToString();
    }

    private static EnvironmentPromptContext CreateContext() =>
        new()
        {
            Session = AgentSession.Create("host-env-test"),
            WorkspaceRoot = @"C:\work\demo",
            Tools = Array.Empty<ToolDefinition>(),
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings()
        };
}
