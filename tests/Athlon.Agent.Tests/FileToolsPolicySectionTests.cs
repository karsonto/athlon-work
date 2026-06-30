using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class FileToolsPolicySectionTests
{
    [Fact]
    public void Append_WorkspaceMode_IncludesFileToolRules()
    {
        var builder = new StringBuilder();
        new FileToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true));

        var text = builder.ToString();
        Assert.Contains("File tools:", text, StringComparison.Ordinal);
        Assert.Contains("grep_files", text, StringComparison.Ordinal);
        Assert.Contains("N| prefixes", text, StringComparison.Ordinal);
        Assert.Contains("character-for-character", text, StringComparison.Ordinal);
        Assert.Contains("CJK characters", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_ChatOnlyMode_DoesNotOutput()
    {
        var builder = new StringBuilder();
        new FileToolsPolicySection().Append(builder, CreateContext(hasWorkspace: false, chatOnly: true));

        Assert.Equal(string.Empty, builder.ToString());
    }

    private static EnvironmentPromptContext CreateContext(bool hasWorkspace, bool chatOnly = false) =>
        new()
        {
            Session = chatOnly
                ? AgentSession.Create("file-tools-chat")
                : AgentSession.Create("file-tools-test").WithWorkspace(@"C:\work\demo"),
            WorkspaceRoot = hasWorkspace && !chatOnly ? @"C:\work\demo" : null,
            Tools = chatOnly
                ? [new ToolDefinition("knowledge_search", "Search knowledge", new Dictionary<string, string>())]
                : [],
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings()
        };
}
