using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class ToolsPolicySectionTests
{
    [Fact]
    public void Append_WorkspaceMode_IncludesGeneralToolRules()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools: []));

        var text = builder.ToString();
        Assert.Contains("Tools:", text, StringComparison.Ordinal);
        Assert.Contains("Shell: cmd.exe only", text, StringComparison.Ordinal);
        Assert.Contains("Native tools via function calling", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_WorkspaceMode_DoesNotDuplicateFileToolGuidance()
    {
        var fileBuilder = new StringBuilder();
        new FileToolsPolicySection().Append(fileBuilder, CreateContext(hasWorkspace: true, tools: []));
        var toolsBuilder = new StringBuilder();
        new ToolsPolicySection().Append(toolsBuilder, CreateContext(hasWorkspace: true, tools: []));

        var fileText = fileBuilder.ToString();
        var toolsText = toolsBuilder.ToString();
        Assert.Contains("character-for-character", fileText, StringComparison.Ordinal);
        Assert.Contains("N| prefixes", fileText, StringComparison.Ordinal);
        Assert.DoesNotContain("N| prefixes", toolsText, StringComparison.Ordinal);
        Assert.DoesNotContain("character-for-character", toolsText, StringComparison.Ordinal);
        Assert.DoesNotContain("Prefer search before file_read", toolsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_WithMcpTools_DoesNotListIndividualMcpTools()
    {
        var tools = new[]
        {
            new ToolDefinition("file_read", "Read a file", ToolSchema.Object().Build()),
            new ToolDefinition("mcp_server__echo", "Echo via MCP", ToolSchema.Object().Build(), Source: "mcp"),
            new ToolDefinition("mcp_server__search", "Search via MCP", ToolSchema.Object().Build(), Source: "mcp")
        };
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools));

        var text = builder.ToString();
        Assert.Contains("advertised only via function schemas", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- mcp_server__echo:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- mcp_server__search:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_AskMode_UsesReadOnlyPolicyWithoutShellGuidance()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools: [], mode: SessionAgentMode.Ask));

        var text = builder.ToString();
        Assert.Contains("read-only", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file_write", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_CodingMode_RequiresTodoBeforeWrites()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools: [], mode: SessionAgentMode.Coding));

        var text = builder.ToString();
        Assert.Contains("todo_write before file_write", text, StringComparison.Ordinal);
        Assert.Contains("  5. Shell:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_AgentMode_DoesNotRequireTodoBeforeWrites()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools: [], mode: SessionAgentMode.Agent));

        var text = builder.ToString();
        Assert.DoesNotContain("todo_write before file_write", text, StringComparison.Ordinal);
        Assert.Contains("  4. Shell:", text, StringComparison.Ordinal);
    }

    private static EnvironmentPromptContext CreateContext(
        bool hasWorkspace,
        IReadOnlyList<ToolDefinition> tools,
        SessionAgentMode mode = SessionAgentMode.Agent) =>
        new()
        {
            Session = AgentSession.Create("tools-policy-test"),
            WorkspaceRoot = hasWorkspace ? @"C:\work\demo" : null,
            Tools = tools,
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings(),
            AgentMode = mode,
        };
}
