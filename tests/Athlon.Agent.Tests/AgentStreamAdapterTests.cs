using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class AgentStreamAdapterTests
{
    private const string SessionId = "session-1";
    private const string RunId = "run-1";
    private const string MessageId = "msg-1";

    [Fact]
    public void CreateRunStarted_emits_run_started()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);

        var events = adapter.CreateRunStarted();

        Assert.Single(events);
        Assert.IsType<AgentStreamEvent.RunStarted>(events[0]);
    }

    [Fact]
    public void TextDelta_opens_single_message_and_appends()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);

        var first = adapter.OnTextDelta(MessageId, "hello ");
        var second = adapter.OnTextDelta(MessageId, "world");

        Assert.Single(first.OfType<AgentStreamEvent.TextMessageStart>());
        Assert.Single(second.OfType<AgentStreamEvent.TextMessageContent>());
        Assert.True(adapter.State.HasActiveTextMessage());
    }

    [Fact]
    public void ToolCallDelta_after_text_ends_message_and_starts_tool()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);
        adapter.OnTextDelta(MessageId, "before");

        var result = adapter.OnToolCallDelta(
            MessageId,
            new StreamingToolCallDelta(0, "call-1", "read_file", "{}"));

        Assert.Contains(result, e => e is AgentStreamEvent.TextMessageEnd);
        Assert.False(adapter.State.HasActiveTextMessage());
        Assert.Contains(result, e => e is AgentStreamEvent.ToolCallStart);
    }

    [Fact]
    public void Text_after_toolCallDelta_uses_new_message_id()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);
        adapter.OnTextDelta(MessageId, "first");
        adapter.OnToolCallDelta(MessageId, new StreamingToolCallDelta(0, "call-1", "read_file", "{}"));

        const string secondMessageId = "msg-2";
        var result = adapter.OnTextDelta(secondMessageId, "second");

        Assert.Contains(result, e => e is AgentStreamEvent.TextMessageStart);
        Assert.True(adapter.State.HasActiveTextMessage());
        Assert.Equal(secondMessageId, adapter.State.CurrentTextMessageId);
    }

    [Fact]
    public void Duplicate_toolCallDelta_only_starts_tool_once()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);
        var delta = new StreamingToolCallDelta(0, "call-1", "read_file", "{}");

        var first = adapter.OnToolCallDelta(MessageId, delta);
        var second = adapter.OnToolCallDelta(MessageId, delta);

        Assert.Single(first.OfType<AgentStreamEvent.ToolCallStart>());
        Assert.Empty(second.OfType<AgentStreamEvent.ToolCallStart>());
        Assert.Single(second.OfType<AgentStreamEvent.ToolCallArgs>());
    }

    [Fact]
    public void FinishRun_ends_open_text_message_and_emits_run_finished()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);
        adapter.OnTextDelta(MessageId, "open");

        var result = adapter.FinishRun();

        Assert.Contains(result, e => e is AgentStreamEvent.TextMessageEnd);
        Assert.Contains(result, e => e is AgentStreamEvent.RunFinished);
        Assert.False(adapter.State.HasActiveTextMessage());
        Assert.Null(adapter.State.ActiveAssistantMessageId);
    }

    [Fact]
    public void ReasoningDelta_before_tool_is_ended_at_tool_boundary()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);
        adapter.OnReasoningDelta(MessageId, "think");

        var result = adapter.OnToolCallDelta(
            MessageId,
            new StreamingToolCallDelta(0, "call-1", "read_file", "{}"));

        Assert.Contains(result, e => e is AgentStreamEvent.ReasoningMessageEnd);
        Assert.False(adapter.State.HasActiveReasoningMessage());
    }

    [Fact]
    public void OnAssistantRoundCompleted_emits_tool_start_and_end()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);
        var message = ChatMessage.CreateWithId(
            MessageId,
            MessageRole.Assistant,
            string.Empty,
            toolCalls: [new AgentToolCall("call-1", "alpha", new Dictionary<string, string>())]);

        var result = adapter.OnAssistantRoundCompleted(message);

        Assert.Contains(result, e => e is AgentStreamEvent.ToolCallStart);
        Assert.Contains(result, e => e is AgentStreamEvent.ToolCallEnd);
    }

    [Fact]
    public void OnToolResult_emits_tool_call_result()
    {
        var adapter = new AgentStreamAdapter(SessionId, RunId);
        var toolCall = new AgentToolCall("call-1", "alpha", new Dictionary<string, string>());
        var toolMessage = ChatMessage.Create(MessageRole.Tool, "ok", MessageId);

        var result = adapter.OnToolResult(toolMessage, toolCall);

        Assert.Contains(result, e => e is AgentStreamEvent.ToolCallResult);
    }
}
