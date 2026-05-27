using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class BuildModelMessagesTests
{
    [Fact]
    public void BuildModelMessages_AssistantWithToolCalls_FollowedByTool_IsValidForApi()
    {
        var toolCallId = "call_abc";
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "list files"),
            ChatMessage.Create(
                MessageRole.Assistant,
                string.Empty,
                toolCalls: new[] { new AgentToolCall(toolCallId, "file_list", new Dictionary<string, string>()) }),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(
                    new AgentToolCall(toolCallId, "file_list", new Dictionary<string, string>()),
                    ToolResult.Success("ok", "file-a.txt")))
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(4, messages.Count);
        Assert.Equal("assistant", messages[2].Role);
        Assert.NotNull(messages[2].ToolCalls);
        Assert.Single(messages[2].ToolCalls!);
        Assert.Equal("tool", messages[3].Role);
        Assert.Equal(toolCallId, messages[3].ToolCallId);
    }

    [Fact]
    public void BuildModelMessages_OrphanToolMessage_EmitsAsUserFallback()
    {
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "hello"),
            ChatMessage.Create(MessageRole.Tool, "ToolCallId: x\nTool `grep` succeeded.\n\nSummary: ok")
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[2].Role);
        Assert.Contains("[Tool output]", messages[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildModelMessages_MultipleToolCalls_EachGetsToolMessage()
    {
        var callA = "call_a";
        var callB = "call_b";
        var toolCalls = new[]
        {
            new AgentToolCall(callA, "file_read", new Dictionary<string, string> { ["path"] = "a.txt" }),
            new AgentToolCall(callB, "file_read", new Dictionary<string, string> { ["path"] = "b.txt" })
        };
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "read both"),
            ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: toolCalls),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(toolCalls[0], ToolResult.Success("ok", "a"))),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(toolCalls[1], ToolResult.Success("ok", "b")))
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(5, messages.Count);
        Assert.Equal(2, messages[2].ToolCalls!.Count);
        Assert.Equal("tool", messages[3].Role);
        Assert.Equal(callA, messages[3].ToolCallId);
        Assert.Equal("tool", messages[4].Role);
        Assert.Equal(callB, messages[4].ToolCallId);
    }

    [Fact]
    public void BuildModelMessages_MissingToolResult_UsesPlaceholderToolMessage()
    {
        var callA = "call_a";
        var callB = "call_b";
        var toolCalls = new[]
        {
            new AgentToolCall(callA, "file_read", new Dictionary<string, string>()),
            new AgentToolCall(callB, "grep_files", new Dictionary<string, string>())
        };
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "run"),
            ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: toolCalls),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(toolCalls[0], ToolResult.Success("ok", "only a")))
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(5, messages.Count);
        Assert.Equal(2, messages[2].ToolCalls!.Count);
        Assert.Equal("tool", messages[3].Role);
        Assert.Equal(callA, messages[3].ToolCallId);
        Assert.Equal("tool", messages[4].Role);
        Assert.Equal(callB, messages[4].ToolCallId);
        Assert.Contains("not recorded", messages[4].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildModelMessages_AssistantWithReasoningContent_PassesThrough()
    {
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "question"),
            ChatMessage.Create(MessageRole.Assistant, "answer", reasoningContent: "thinking chain")
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(3, messages.Count);
        Assert.Equal("assistant", messages[2].Role);
        Assert.Equal("thinking chain", messages[2].ReasoningContent);
    }

    [Fact]
    public void BuildModelMessages_SkipsCompactionAudit()
    {
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "hi"),
            ChatMessage.Create(MessageRole.Compaction, "CompactionKind: microcompact\n\nSummary: cleared")
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(messages, message => message.Content.Contains("microcompact", StringComparison.Ordinal));
    }
}
