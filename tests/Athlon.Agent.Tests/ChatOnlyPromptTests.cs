using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class ChatOnlyPromptTests
{
    [Fact]
    public void PrepareForTurn_ChatOnly_NoTools_UsesAssistantPersonaWithoutCodingGuidance()
    {
        var orchestrator = PromptTestHelpers.CreateOrchestrator(new PromptTestHelpers.FakeHostEnvironment(
            @"C:\Users\test\.athlon-agent\skills",
            @"C:\Users\test\.athlon-agent"));

        var session = AgentSession.Create("chat-only");
        var prompt = orchestrator.PrepareForTurn(session, Array.Empty<ToolDefinition>()).Text;

        Assert.Contains("AI assistant", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coding agent", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace files", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file_read", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("execute_command", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("文件工具仍可使用", prompt, StringComparison.Ordinal);
        Assert.Contains("纯对话模式", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareForTurn_ChatOnly_WithKnowledgeTool_IncludesKnowledgeGuidanceOnly()
    {
        var orchestrator = PromptTestHelpers.CreateOrchestrator(new PromptTestHelpers.FakeHostEnvironment(
            @"C:\Users\test\.athlon-agent\skills",
            @"C:\Users\test\.athlon-agent"));

        var session = AgentSession.Create("chat-only-kb");
        var tools = new[]
        {
            new ToolDefinition("knowledge_search", "Search knowledge base", new Dictionary<string, string> { ["query"] = "query" })
        };

        var prompt = orchestrator.PrepareForTurn(session, tools).Text;

        Assert.Contains("AI assistant", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("knowledge_search", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("coding agent", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File tools:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("load_skill_through_path", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareForTurn_WithWorkspace_UsesCodingAgentPersonaAndFileToolsGuidance()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"athlon-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var settings = new AppSettings
            {
                Workspaces =
                [
                    new WorkspaceSettings
                    {
                        Name = "demo",
                        RootPath = workspaceRoot
                    }
                ]
            };

            var orchestrator = PromptTestHelpers.CreateOrchestrator(
                new PromptTestHelpers.FakeHostEnvironment(
                    @"C:\Users\test\.athlon-agent\skills",
                    @"C:\Users\test\.athlon-agent"),
                settings);

            var session = AgentSession.Create("workspace-mode").WithWorkspace(workspaceRoot);
            var tools = new[]
            {
                new ToolDefinition("file_read", "Read a file", new Dictionary<string, string> { ["path"] = "path" })
            };

            var prompt = orchestrator.PrepareForTurn(session, tools).Text;

            Assert.Contains("workspace agent", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Session mode:", prompt, StringComparison.Ordinal);
            Assert.Contains("Agent mode", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("File tools:", prompt, StringComparison.Ordinal);
            Assert.Contains($"Workspace root: {workspaceRoot}", prompt, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }
}
