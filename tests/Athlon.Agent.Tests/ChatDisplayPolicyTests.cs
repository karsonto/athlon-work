using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class ChatDisplayPolicyTests
{
    [Fact]
    public void ShouldIncludeToolMessage_excludes_tool_when_disabled()
    {
        var tool = ChatMessage.Create(MessageRole.Tool, "ToolCallId: x\nTool `read_file` succeeded.");
        var user = ChatMessage.Create(MessageRole.User, "hello");
        var compaction = CompactionMessageContent.CreateCompactionMessage(
            CompactionMessageContent.CreateConversationCompact(1000, 500, 3, null, "summary"));

        Assert.False(ChatDisplayPolicy.ShouldIncludeToolMessage(showToolCalls: false, tool));
        Assert.True(ChatDisplayPolicy.ShouldIncludeToolMessage(showToolCalls: false, user));
        Assert.True(ChatDisplayPolicy.ShouldIncludeToolMessage(showToolCalls: false, compaction));
        Assert.True(ChatDisplayPolicy.ShouldIncludeToolMessage(showToolCalls: true, tool));
    }

    [Fact]
    public void ShouldIncludeToolViewModel_keeps_compaction_when_tools_hidden()
    {
        var compactionVm = new ChatMessageViewModel(
            CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(1000, 500, 3, null, "summary")));
        var toolVm = new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.Tool, "ToolCallId: x\nTool `read_file` succeeded."));

        Assert.True(ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: false, compactionVm));
        Assert.False(ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: false, toolVm));
        Assert.True(ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: true, toolVm));
    }

    [Fact]
    public void BuildDisplayMessages_hides_tools_by_default()
    {
        var toolCall = new AgentToolCall("call-1", "read_file", new Dictionary<string, string>());
        var toolContent = AgentRuntime.FormatToolResult(toolCall, ToolResult.Success("ok", "detail"));
        var compaction = CompactionMessageContent.CreateCompactionMessage(
            CompactionMessageContent.CreateConversationCompact(1000, 500, 3, null, "summary"));
        var displayMessages = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "hello"),
            ChatMessage.Create(MessageRole.Tool, toolContent),
            compaction,
            ChatMessage.Create(MessageRole.Assistant, "done")
        };

        var hidden = ChatTimelineHydrator.BuildDisplayMessages(displayMessages, showToolCalls: false);
        Assert.DoesNotContain(hidden, vm => vm.IsTool && !vm.IsCompaction);
        Assert.Contains(hidden, vm => vm.IsCompaction);
        Assert.Equal(3, hidden.Count);

        var shown = ChatTimelineHydrator.BuildDisplayMessages(displayMessages, showToolCalls: true);
        Assert.Contains(shown, vm => vm.IsTool && !vm.IsCompaction);
        Assert.Equal(4, shown.Count);
    }

    [Fact]
    public void BuildReplayEvents_hides_tool_events_when_disabled()
    {
        var toolCall = new AgentToolCall("call-1", "read_file", new Dictionary<string, string>());
        var toolContent = AgentRuntime.FormatToolResult(toolCall, ToolResult.Success("ok", "detail"));
        var messages = new List<ChatMessageViewModel>
        {
            new(ChatMessage.Create(MessageRole.User, "hello")),
            new(ChatMessage.Create(MessageRole.Tool, toolContent)),
            new(CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(1000, 500, 3, null, "summary")))
        };

        var hidden = ChatEventSerializer.BuildReplayEvents(messages, showToolCalls: false);
        Assert.DoesNotContain(hidden, json => json.Contains("read_file", StringComparison.Ordinal));
        Assert.Equal(1, hidden.Count(json => json.Contains("TOOL_CALL_START", StringComparison.Ordinal)));

        var shown = ChatEventSerializer.BuildReplayEvents(messages, showToolCalls: true);
        Assert.Contains(shown, json => json.Contains("read_file", StringComparison.Ordinal));
        Assert.Equal(2, shown.Count(json => json.Contains("TOOL_CALL_START", StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildReplayEvents_includes_running_pending_manual_compaction()
    {
        var pending = ChatMessageViewModel.CreatePendingManualCompaction();
        var events = ChatEventSerializer.BuildReplayEvents([pending], showToolCalls: false);

        Assert.Contains(events, json => json.Contains("TOOL_CALL_START", StringComparison.Ordinal));
        Assert.Contains(events, json => json.Contains(ChatMessageViewModel.PendingManualCompactionMessageId, StringComparison.Ordinal));
        Assert.Contains(events, json => json.Contains("TOOL_CALL_END", StringComparison.Ordinal));
        Assert.DoesNotContain(events, json => json.Contains("TOOL_CALL_RESULT", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReplayEvents_includes_failed_tool_status()
    {
        var toolCall = new AgentToolCall("call-fail", "read_file", new Dictionary<string, string>());
        var toolContent = AgentRuntime.FormatToolResult(toolCall, ToolResult.Failure("Read failed", "file not found"));
        var messages = new List<ChatMessageViewModel>
        {
            new(ChatMessage.Create(MessageRole.Tool, toolContent))
        };

        var events = ChatEventSerializer.BuildReplayEvents(messages, showToolCalls: true);
        var resultEvent = Assert.Single(events, json => json.Contains("TOOL_CALL_RESULT", StringComparison.Ordinal));
        Assert.Contains("\"status\":\"failed\"", resultEvent, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_includes_failed_status_for_tool_call_result_stream_event()
    {
        var toolCall = new AgentToolCall("call-fail", "read_file", new Dictionary<string, string>());
        var toolContent = AgentRuntime.FormatToolResult(toolCall, ToolResult.Failure("Read failed", "file not found"));
        var streamEvent = new AgentStreamEvent.ToolCallResult("call-fail", toolContent, "msg-fail");

        var json = ChatEventSerializer.Serialize(streamEvent);

        Assert.Contains("\"status\":\"failed\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void IsToolStreamEvent_matches_tool_stream_types()
    {
        Assert.True(ChatDisplayPolicy.IsToolStreamEvent(new AgentStreamEvent.ToolCallStart("id", "tool", 0)));
        Assert.True(ChatDisplayPolicy.IsToolStreamEvent(new AgentStreamEvent.ToolCallArgs("id", "{}")));
        Assert.False(ChatDisplayPolicy.IsToolStreamEvent(new AgentStreamEvent.TextMessageContent("id", "hi")));
    }
}
