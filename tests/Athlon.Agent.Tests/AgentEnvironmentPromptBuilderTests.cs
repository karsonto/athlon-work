using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class AgentEnvironmentPromptBuilderTests
{
    [Fact]
    public void Build_IncludesWindowsHostEnvironmentAndSkillsDirectory()
    {
        var skillsPath = @"C:\Users\test\.athlon-agent\skills";
        var appDataPath = @"C:\Users\test\.athlon-agent";
        var builder = new AgentEnvironmentPromptBuilder(
            new AppSettings(),
            new FixedSkillsProvider(),
            new FakeHostEnvironment(skillsPath, appDataPath));

        var prompt = builder.Build(AgentSession.Create("prompt-test"), Array.Empty<ToolDefinition>());

        Assert.Contains("Host environment (current Windows user session):", prompt, StringComparison.Ordinal);
        Assert.Contains("- Current date/time (local):", prompt, StringComparison.Ordinal);
        Assert.Contains("- Current date/time (UTC):", prompt, StringComparison.Ordinal);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", prompt);
        Assert.Contains(@"User: TESTDOMAIN\karson", prompt, StringComparison.Ordinal);
        Assert.Contains($"- Default skills directory: {skillsPath}", prompt, StringComparison.Ordinal);
        Assert.Contains($"- Agent app data: {appDataPath}", prompt, StringComparison.Ordinal);
        Assert.Contains($"none installed under {skillsPath}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("~/.athlon-agent/skills", prompt, StringComparison.Ordinal);
        Assert.Contains("On Windows, execute commands with cmd/cmd.exe semantics; do not use PowerShell syntax or PowerShell-specific commands.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("use PowerShell", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ListsOnlyEnabledMcpTools_NotDisabledServerConfig()
    {
        var settings = new AppSettings
        {
            McpServers =
            {
                new McpServerSettings { Name = "disabled-server", Enabled = false, Command = "npx", Args = ["-y", "secret-mcp"] },
                new McpServerSettings { Name = "enabled-server", Enabled = true, Command = "npx", Args = ["-y", "active-mcp"] }
            }
        };
        var builder = new AgentEnvironmentPromptBuilder(
            settings,
            new FixedSkillsProvider(),
            new FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"));

        var tools = new[]
        {
            new ToolDefinition("file_read", "Read a file", new Dictionary<string, string>()),
            new ToolDefinition("mcp_enabled-server__echo", "Echo via MCP", new Dictionary<string, string>(), Source: "mcp")
        };

        var prompt = builder.Build(AgentSession.Create("mcp-prompt-test"), tools);

        var nativeStart = prompt.IndexOf("Available native tools:", StringComparison.Ordinal);
        var mcpStart = prompt.IndexOf("Available MCP tools:", StringComparison.Ordinal);
        var nativeSection = prompt[nativeStart..mcpStart];
        Assert.Contains("- file_read: Read a file", nativeSection, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_enabled-server__echo", nativeSection, StringComparison.Ordinal);

        Assert.Contains("Available MCP tools:", prompt, StringComparison.Ordinal);
        Assert.Contains("- mcp_enabled-server__echo: Echo via MCP", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MCP server status:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled-server", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-mcp", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("active-mcp", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithWorkspace_IncludesPlanningGuidance()
    {
        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = @"C:\work\demo" } }
        };
        var builder = new AgentEnvironmentPromptBuilder(
            settings,
            new FixedSkillsProvider(),
            new FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"));

        var prompt = builder.Build(AgentSession.Create("plan-test"), Array.Empty<ToolDefinition>());

        Assert.Contains("Think through the user's goal", prompt, StringComparison.Ordinal);
        Assert.Contains("plan.md", prompt, StringComparison.Ordinal);
        Assert.Contains("Execute one step at a time", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_IncludesMermaidGuidance()
    {
        var builder = new AgentEnvironmentPromptBuilder(
            new AppSettings(),
            new FixedSkillsProvider(),
            new FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"));

        var prompt = builder.Build(AgentSession.Create("mermaid-test"), Array.Empty<ToolDefinition>());

        Assert.Contains("Mermaid diagrams in chat:", prompt, StringComparison.Ordinal);
        Assert.Contains("```mermaid", prompt, StringComparison.Ordinal);
        Assert.Contains("sequenceDiagram", prompt, StringComparison.Ordinal);
        Assert.Contains("查看 Mermaid 图表", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenNoMcpTools_ShowsBriefMcpSection()
    {
        var builder = new AgentEnvironmentPromptBuilder(
            new AppSettings(),
            new FixedSkillsProvider(),
            new FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"));

        var prompt = builder.Build(
            AgentSession.Create("no-mcp"),
            [new ToolDefinition("file_list", "List files", new Dictionary<string, string>())]);

        Assert.Contains("Available MCP tools:", prompt, StringComparison.Ordinal);
        Assert.Contains("none (no enabled MCP servers with tools).", prompt, StringComparison.Ordinal);
    }

    private sealed class FixedSkillsProvider : IAvailableSkillsProvider
    {
        public IReadOnlyList<AvailableSkillInfo> GetSkills() => Array.Empty<AvailableSkillInfo>();
    }

    private sealed class FakeHostEnvironment(string skillsDirectory, string appDataDirectory) : IAgentHostEnvironment
    {
        public bool IsWindows => true;
        public string OsDescription => "Microsoft Windows 11";
        public string OsVersion => "10.0.22631.0";
        public string UserName => "karson";
        public string UserDomainName => "TESTDOMAIN";
        public string MachineName => "DESKTOP-TEST";
        public string UserProfilePath => @"C:\Users\karson";
        public string CurrentDirectory => @"C:\Users\karson\athlon-work";
        public string SystemDirectory => @"C:\Windows\system32";
        public string ProcessArchitecture => "X64";
        public string OsArchitecture => "X64";
        public int ProcessorCount => 8;
        public string AppDataDirectory { get; } = appDataDirectory;
        public string SkillsDirectory { get; } = skillsDirectory;
    }
}
