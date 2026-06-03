using Athlon.Agent.Core;
using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;

namespace Athlon.Agent.Tests;

public sealed class AgentEnvironmentPromptBuilderTests
{
    [Fact]
    public void Build_IncludesWindowsHostEnvironmentAndSkillsDirectory()
    {
        var skillsPath = @"C:\Users\test\.athlon-agent\skills";
        var appDataPath = @"C:\Users\test\.athlon-agent";
        var builder = PromptTestHelpers.CreateBuilder(new PromptTestHelpers.FakeHostEnvironment(skillsPath, appDataPath));

        var prompt = builder.Build(AgentSession.Create("prompt-test"), Array.Empty<ToolDefinition>());

        Assert.Contains("Host: Win", prompt, StringComparison.Ordinal);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", prompt);
        Assert.Contains(@"TESTDOMAIN\karson", prompt, StringComparison.Ordinal);
        Assert.Contains($"skills={skillsPath}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain($"none installed under {skillsPath}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("~/.athlon-agent/skills", prompt, StringComparison.Ordinal);
        Assert.Contains("Windows: cmd.exe only, not PowerShell.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("prefer PowerShell", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_SummarizesNativeTools_ListsMcpTools()
    {
        var builder = PromptTestHelpers.CreateBuilder(
            new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"));

        var tools = new[]
        {
            new ToolDefinition("file_read", "Read a file", new Dictionary<string, string>()),
            new ToolDefinition("mcp_enabled-server__echo", "Echo via MCP", new Dictionary<string, string>(), Source: "mcp")
        };

        var prompt = builder.Build(AgentSession.Create("mcp-prompt-test"), tools);

        Assert.Contains("Native tools are provided via function calling", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("file_list, file_read", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("- file_read: Read a file", prompt, StringComparison.Ordinal);
        Assert.Contains("Available MCP tools:", prompt, StringComparison.Ordinal);
        Assert.Contains("- mcp_enabled-server__echo: Echo via MCP", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithWorkspace_ExcludesPlanningGuidance()
    {
        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = @"C:\work\demo" } }
        };
        var builder = PromptTestHelpers.CreateBuilder(
            new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            settings);

        var prompt = builder.Build(AgentSession.Create("plan-test"), Array.Empty<ToolDefinition>());

        Assert.DoesNotContain("Plan mode (spec-first workflow)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("create_plan", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithWorkspace_InjectsAgentsMd()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-prompt-ws", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Project rules\nUse plan.md.");

        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = root } }
        };

        try
        {
            var builder = PromptTestHelpers.CreateBuilder(
                new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
                settings);

            var prompt = builder.Build(AgentSession.Create("agents-md"), Array.Empty<ToolDefinition>());
            Assert.Contains("## AGENTS.md", prompt, StringComparison.Ordinal);
            Assert.Contains("# Project rules", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Build_IncludesMermaidGuidance()
    {
        var builder = PromptTestHelpers.CreateBuilder(
            new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"));

        var prompt = builder.Build(AgentSession.Create("mermaid-test"), Array.Empty<ToolDefinition>());

        Assert.Contains("Mermaid diagrams in chat:", prompt, StringComparison.Ordinal);
        Assert.Contains("```mermaid", prompt, StringComparison.Ordinal);
        Assert.Contains("sequenceDiagram", prompt, StringComparison.Ordinal);
        Assert.Contains("查看 Mermaid 图表", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenNoMcpTools_OmitsMcpSection()
    {
        var builder = PromptTestHelpers.CreateBuilder(
            new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"));

        var prompt = builder.Build(
            AgentSession.Create("no-mcp"),
            [new ToolDefinition("file_list", "List files", new Dictionary<string, string>())]);

        Assert.DoesNotContain("Available MCP tools:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("none (no enabled MCP servers with tools).", prompt, StringComparison.Ordinal);
    }
}
