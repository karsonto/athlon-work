using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class RunningToolPlaceholderTests
{
    [Fact]
    public void ToolResultMessageId_IsStableForSameToolCallId()
    {
        Assert.Equal("tool:call-1", ChatMessage.ToolResultMessageId("call-1"));
        Assert.Equal(
            ChatMessage.ToolResultMessageId("abc"),
            ChatMessage.ToolResultMessageId("abc"));
    }

    [Fact]
    public void WithUpsertedMessage_ReplacesSameId()
    {
        var session = AgentSession.Create("s");
        var first = ChatMessage.CreateWithId("tool:call-1", MessageRole.Tool, "running");
        var second = ChatMessage.CreateWithId("tool:call-1", MessageRole.Tool, "done");

        session = session.WithUpsertedMessage(first).WithUpsertedMessage(second);

        var tool = Assert.Single(session.Messages);
        Assert.Equal("done", tool.Content);
    }

    [Fact]
    public void FormatRunningToolPlaceholder_IsDetectedAsRunning()
    {
        var call = new AgentToolCall("c1", "grep", new Dictionary<string, string> { ["pattern"] = "x" });
        var content = ModelMessageBuilder.FormatRunningToolPlaceholder(call);

        Assert.True(ModelMessageBuilder.IsRunningToolResult(content));
        Assert.Equal("c1", ModelMessageBuilder.ExtractToolCallId(content));
        Assert.Contains("running.", content, StringComparison.Ordinal);
        Assert.Contains("Summary: 执行中", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildModelMessages_SkipsRunningToolPlaceholder()
    {
        var call = new AgentToolCall("c1", "grep", new Dictionary<string, string>());
        var user = ChatMessage.Create(MessageRole.User, "search");
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: [call]);
        var running = ChatMessage.CreateWithId(
            ChatMessage.ToolResultMessageId(call.Id),
            MessageRole.Tool,
            ModelMessageBuilder.FormatRunningToolPlaceholder(call),
            user.Id);

        var messages = AgentRuntime.BuildModelMessages(
            "system",
            [user, assistant, running]);

        Assert.DoesNotContain(
            messages,
            message => message.Role == "tool"
                && message.Content.ToString()!.Contains("执行中", StringComparison.Ordinal));
        Assert.Contains(
            messages,
            message => message.Role == "tool"
                && message.ToolCallId == call.Id
                && message.Content.ToString()!.Contains("did not run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FormatToolResult_IsNotRunning()
    {
        var content = ModelMessageBuilder.FormatToolResult(
            new AgentToolCall("c1", "grep", new Dictionary<string, string>()),
            ToolResult.Success("ok", "hits"));

        Assert.False(ModelMessageBuilder.IsRunningToolResult(content));
    }
}
