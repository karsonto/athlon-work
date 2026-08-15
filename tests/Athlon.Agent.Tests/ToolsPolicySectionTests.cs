using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class ToolsPolicySectionTests
{
    private static readonly ToolDefinition[] FullWorkspaceTools =
    [
        new("file_read", "r", ToolSchema.Object().Build()),
        new("file_write", "w", ToolSchema.Object().Build()),
        new("file_edit", "e", ToolSchema.Object().Build()),
        new("apply_patch", "p", ToolSchema.Object().Build()),
        new("execute_command", "x", ToolSchema.Object().Build()),
        new("todo_write", "t", ToolSchema.Object().Build()),
        new("create_plan", "c", ToolSchema.Object().Build()),
        new("update_plan", "u", ToolSchema.Object().Build()),
        new("mcp_search", "ms", ToolSchema.Object().Build()),
        new("mcp_call", "mc", ToolSchema.Object().Build()),
    ];

    [Fact]
    public void Append_WorkspaceMode_IncludesGeneralToolRules()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(builder, CreateContext(hasWorkspace: true, tools: FullWorkspaceTools));

        var text = builder.ToString();
        Assert.Contains("Tools:", text, StringComparison.Ordinal);
        Assert.Contains("Shell: cmd.exe only", text, StringComparison.Ordinal);
        Assert.Contains("Native tools via function calling", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_WorkspaceMode_DoesNotDuplicateFileToolGuidance()
    {
        var fileBuilder = new StringBuilder();
        new FileToolsPolicySection().Append(fileBuilder, CreateContext(hasWorkspace: true, tools: FullWorkspaceTools));
        var toolsBuilder = new StringBuilder();
        new ToolsPolicySection().Append(toolsBuilder, CreateContext(hasWorkspace: true, tools: FullWorkspaceTools));

        var fileText = fileBuilder.ToString();
        var toolsText = toolsBuilder.ToString();
        Assert.Contains("character-for-character", fileText, StringComparison.Ordinal);
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
        new ToolsPolicySection().Append(
            builder,
            CreateContext(hasWorkspace: true, tools: FullWorkspaceTools, mode: SessionAgentMode.Ask));

        var text = builder.ToString();
        Assert.Contains("read-only", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reject mutation", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_CodingMode_RequiresTodoMaintenanceForWrites()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(
            builder,
            CreateContext(hasWorkspace: true, tools: FullWorkspaceTools, mode: SessionAgentMode.Coding));

        var text = builder.ToString();
        Assert.Contains("todo_write", text, StringComparison.Ordinal);
        Assert.Contains("maintain an accurate todo list", text, StringComparison.Ordinal);
        Assert.Contains("Shell:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_AgentMode_DoesNotRequireTodoBeforeWrites()
    {
        var tools = FullWorkspaceTools.Where(tool => tool.Name != "todo_write").ToArray();
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(
            builder,
            CreateContext(hasWorkspace: true, tools: tools, mode: SessionAgentMode.Agent));

        var text = builder.ToString();
        Assert.DoesNotContain("maintain an accurate todo list", text, StringComparison.Ordinal);
        Assert.Contains("Shell:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_PlanMode_UsesReadOnlyPolicyWithCreatePlan()
    {
        var builder = new StringBuilder();
        new ToolsPolicySection().Append(
            builder,
            CreateContext(hasWorkspace: true, tools: FullWorkspaceTools, mode: SessionAgentMode.Plan));

        var text = builder.ToString();
        Assert.Contains("read-only", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create_plan", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", text, StringComparison.Ordinal);
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
