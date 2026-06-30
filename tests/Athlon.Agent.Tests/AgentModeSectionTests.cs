using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class AgentModeSectionTests
{
    [Theory]
    [InlineData(SessionAgentMode.Agent, "Agent mode")]
    [InlineData(SessionAgentMode.Coding, "Coding mode")]
    [InlineData(SessionAgentMode.Ask, "Ask mode")]
    public void Append_WithWorkspace_IncludesModeDeclaration(SessionAgentMode mode, string expectedPhrase)
    {
        var builder = new StringBuilder();
        new AgentModeSection().Append(builder, CreateContext(mode));

        var text = builder.ToString();
        Assert.Contains("Session mode:", text, StringComparison.Ordinal);
        Assert.Contains(expectedPhrase, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_ChatOnly_SkipsContent()
    {
        var builder = new StringBuilder();
        new AgentModeSection().Append(builder, CreateContext(SessionAgentMode.Ask, hasWorkspace: false));

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_AskMode_MentionsBlockedTools()
    {
        var builder = new StringBuilder();
        new AgentModeSection().Append(builder, CreateContext(SessionAgentMode.Ask));

        var text = builder.ToString();
        Assert.Contains("file_write", text, StringComparison.Ordinal);
        Assert.Contains("execute_command", text, StringComparison.Ordinal);
        Assert.Contains("sessions_", text, StringComparison.Ordinal);
    }

    private static EnvironmentPromptContext CreateContext(SessionAgentMode mode, bool hasWorkspace = true) =>
        new()
        {
            Session = AgentSession.Create("agent-mode-test"),
            WorkspaceRoot = hasWorkspace ? @"C:\work\demo" : null,
            Tools = [],
            SkillsDirectory = @"C:\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\skills", @"C:\app"),
            PromptSettings = new PromptSettings(),
            AgentMode = mode,
        };
}
