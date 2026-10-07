using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class PrefixCacheStabilityTests
{
    [Fact]
    public void PrepareForTurn_AgentMode_IgnoresBrowserAndMcpTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-prefix-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Project rules\nKeep the prefix stable.");

        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = root } }
        };

        try
        {
            var orchestrator = PromptTestHelpers.CreateOrchestrator(
                new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
                settings);
            var session = AgentSession.Create("prefix-cache").WithWorkspace(root);
            var baseline = new ToolDefinition[]
            {
                new("file_read", "r", ToolSchema.Object().Build()),
                new("execute_command", "s", ToolSchema.Object().Build()),
            };
            var withVolatileTools = baseline.Concat(
            [
                new ToolDefinition("browser_navigate", "open", ToolSchema.Object().Build()),
                new ToolDefinition(
                    "mcp_search",
                    "search",
                    ToolSchema.Object().Build(),
                    Source: "mcp",
                    Group: ToolGroup.Mcp),
            ]).ToArray();

            var without = orchestrator.PrepareForTurn(session, baseline).Text;
            var with = orchestrator.PrepareForTurn(session, withVolatileTools).Text;

            Assert.Equal(without, with);
            Assert.DoesNotContain("Working directory (workspace root)", without, StringComparison.Ordinal);
            Assert.Contains("Workspace root:", orchestrator.BuildRuntimeContext(session, baseline), StringComparison.Ordinal);

            var product = without.IndexOf("Durable facts such as preferences", StringComparison.Ordinal);
            var agents = without.IndexOf("## AGENTS.md", StringComparison.Ordinal);
            Assert.True(product >= 0);
            Assert.True(agents > product);
            Assert.True(PromptSectionBands.Product < PromptSectionBands.Skills);
            Assert.True(PromptSectionBands.Skills < PromptSectionBands.WorkspaceFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Build_keeps_history_tool_text_and_puts_newest_ui_trees_at_the_end()
    {
        var cache = new ModelMessageCache();
        var history = new List<ChatMessage>();
        history.AddRange(Observe("call-1", "frame-1"));
        history.AddRange(Observe("call-2", "frame-2"));
        history.AddRange(Observe("call-3", "frame-3"));

        var settings = new ContextCompactionSettings();
        var state = new RuntimeContextInjectionState();
        var first = ModelMessagesForApiBuilder.Build(cache, "system", history, settings, "runtime", state);
        Assert.True(state.FingerprintChanged);
        AssertTrees(first.Messages, retainedFrames: ["frame-2", "frame-3"], droppedFrame: "frame-1");
        var firstToolText = ToolTexts(first.Messages);

        history.AddRange(Observe("call-4", "frame-4"));
        var second = ModelMessagesForApiBuilder.Build(cache, "system", history, settings, "runtime", state);

        Assert.False(state.FingerprintChanged);
        Assert.Equal(firstToolText, ToolTexts(second.Messages).Take(firstToolText.Count));
        AssertTrees(second.Messages, retainedFrames: ["frame-3", "frame-4"], droppedFrame: "frame-2");
    }

    private static void AssertTrees(
        IReadOnlyList<AgentModelMessage> messages,
        string[] retainedFrames,
        string droppedFrame)
    {
        var runtimeIndex = messages.ToList().FindIndex(message => Equals(message.Content, "runtime"));
        Assert.True(runtimeIndex > 0);

        foreach (var tool in messages.Take(runtimeIndex).Where(message => message.Role == "tool"))
        {
            var text = Assert.IsType<string>(tool.Content);
            Assert.Contains("ui_tree stripped from history", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"element_id\"", text, StringComparison.Ordinal);
        }

        var suffix = string.Join(
            "\n",
            messages.Skip(runtimeIndex + 1).Select(message => message.Content as string ?? string.Empty));
        Assert.Contains("Retained UI tree", suffix, StringComparison.Ordinal);
        foreach (var frame in retainedFrames)
        {
            Assert.Contains($"\"element_id\":\"{frame}\"", suffix, StringComparison.Ordinal);
        }

        Assert.DoesNotContain($"\"element_id\":\"{droppedFrame}\"", suffix, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ToolTexts(IReadOnlyList<AgentModelMessage> messages) =>
        messages
            .Where(message => message.Role == "tool")
            .Select(message => Assert.IsType<string>(message.Content))
            .ToArray();

    private static IEnumerable<ChatMessage> Observe(string id, string frame)
    {
        var body = $$"""
            {
              "frame_id": "{{frame}}",
              "ui_tree": [{"element_id":"{{frame}}","name":"Window"}]
            }
            """;
        var call = new AgentToolCall(id, "computer_observe", ToolCallArguments.Empty);
        yield return ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: [call]);
        yield return ChatMessage.Create(
            MessageRole.Tool,
            AgentRuntime.FormatToolResult(call, ToolResult.Success("ok", body)));
    }
}
