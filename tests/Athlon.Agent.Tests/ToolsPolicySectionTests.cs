using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class ToolsPolicySectionTests
{
    [Fact]
    public void Append_WorkspaceMode_IncludesMergedFileAndShellRules()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools: []));

        var text = builder.ToString();
        Assert.Contains("Tools:", text, StringComparison.Ordinal);
        Assert.Contains("grep_files", text, StringComparison.Ordinal);
        Assert.Contains("file_edit old_text", text, StringComparison.Ordinal);
        Assert.Contains("Shell: cmd.exe only", text, StringComparison.Ordinal);
        Assert.Contains("CJK", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_WorkspaceMode_DoesNotDuplicatePathGuidance()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools: []));

        var text = builder.ToString();
        Assert.Equal(1, CountOccurrences(text, "copy verbatim from file_list"));
    }

    [Fact]
    public void Append_WithMcpTools_DoesNotListIndividualMcpTools()
    {
        var tools = new[]
        {
            new ToolDefinition("file_read", "Read a file", new Dictionary<string, string>()),
            new ToolDefinition("mcp_server__echo", "Echo via MCP", new Dictionary<string, string>(), Source: "mcp"),
            new ToolDefinition("mcp_server__search", "Search via MCP", new Dictionary<string, string>(), Source: "mcp")
        };
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools));

        var text = builder.ToString();
        Assert.Contains("advertised only via function schemas", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- mcp_server__echo:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- mcp_server__search:", text, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static EnvironmentPromptContext CreateContext(bool hasWorkspace, IReadOnlyList<ToolDefinition> tools) =>
        new()
        {
            Session = AgentSession.Create("tools-policy-test"),
            WorkspaceRoot = hasWorkspace ? @"C:\work\demo" : null,
            Tools = tools,
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings()
        };
}
