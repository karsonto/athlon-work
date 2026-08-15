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
    [InlineData(SessionAgentMode.Plan, "Session Plan mode")]
    public void Append_WithWorkspace_IncludesModeDeclaration(SessionAgentMode mode, string expectedPhrase)
    {
        var builder = new StringBuilder();
        new AgentModeSection().Append(builder, CreateContext(mode));

        var text = builder.ToString();
        Assert.Contains("Session mode:", text, StringComparison.Ordinal);
        Assert.Contains(expectedPhrase, text, StringComparison.Ordinal);
        if (mode == SessionAgentMode.Coding)
        {
            Assert.Contains("maintain todos with todo_write", text, StringComparison.Ordinal);
            Assert.Contains("Direct Coding without a prior Plan is allowed", text, StringComparison.Ordinal);
        }

        if (mode == SessionAgentMode.Plan)
        {
            Assert.Contains("create_plan", text, StringComparison.Ordinal);
            Assert.Contains("wait for the user to confirm", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Append_ChatOnly_SkipsContent()
    {
        var builder = new StringBuilder();
        new AgentModeSection().Append(builder, CreateContext(SessionAgentMode.Ask, hasWorkspace: false));

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_AskMode_DelegatesToolRulesToSingleDecisionTree()
    {
        var builder = new StringBuilder();
        new AgentModeSection().Append(builder, CreateContext(SessionAgentMode.Ask));

        var text = builder.ToString();
        Assert.Contains("tool decision tree", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file_write", text, StringComparison.Ordinal);
        Assert.DoesNotContain("execute_command", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sessions_", text, StringComparison.Ordinal);
    }

    private static EnvironmentPromptContext CreateContext(SessionAgentMode mode, bool hasWorkspace = true)
    {
        var tools = new List<ToolDefinition>
        {
            new("file_read", "r", ToolSchema.Object().Build()),
            new("grep_files", "g", ToolSchema.Object().Build()),
        };
        if (mode == SessionAgentMode.Coding)
        {
            tools.Add(new ToolDefinition("todo_write", "t", ToolSchema.Object().Build()));
        }

        if (mode == SessionAgentMode.Plan)
        {
            tools.Add(new ToolDefinition("create_plan", "c", ToolSchema.Object().Build()));
            tools.Add(new ToolDefinition("update_plan", "u", ToolSchema.Object().Build()));
        }

        return new EnvironmentPromptContext
        {
            Session = AgentSession.Create("agent-mode-test"),
            WorkspaceRoot = hasWorkspace ? @"C:\work\demo" : null,
            Tools = tools,
            SkillsDirectory = @"C:\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\skills", @"C:\app"),
            PromptSettings = new PromptSettings(),
            AgentMode = mode,
        };
    }
}
