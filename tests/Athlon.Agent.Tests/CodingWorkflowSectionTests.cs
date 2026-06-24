using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class CodingWorkflowSectionTests
{
    [Fact]
    public void Append_SkipsContent_InChatOnlyMode()
    {
        var builder = new StringBuilder();
        new CodingWorkflowSection().Append(builder, CreateContext(hasWorkspace: false));

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_IncludesVerificationAndDotnet_InCodingMode()
    {
        var builder = new StringBuilder();
        new CodingWorkflowSection().Append(builder, CreateContext(hasWorkspace: true));

        var text = builder.ToString();
        Assert.Contains("Coding workflow:", text, StringComparison.Ordinal);
        Assert.Contains("Verification:", text, StringComparison.Ordinal);
        Assert.Contains("dotnet build", text, StringComparison.Ordinal);
        Assert.Contains("dotnet test", text, StringComparison.Ordinal);
        Assert.Contains("apply_patch", text, StringComparison.Ordinal);
    }

    private static EnvironmentPromptContext CreateContext(bool hasWorkspace) =>
        new()
        {
            Session = AgentSession.Create("coding-workflow-test"),
            WorkspaceRoot = hasWorkspace ? @"C:\work\demo" : null,
            WorkspaceName = hasWorkspace ? "demo" : null,
            IgnorePatterns = [".git"],
            Tools =
            [
                new ToolDefinition("file_read", "Read", new Dictionary<string, string>()),
                new ToolDefinition("execute_command", "Run", new Dictionary<string, string>())
            ],
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings()
        };
}
