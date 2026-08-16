using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;
using System.Text.Json;

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
        // Folded activity tools stay out of the tool-card stream even when ShowToolCalls is on.
        Assert.False(ChatDisplayPolicy.ShouldIncludeToolMessage(showToolCalls: true, tool));
    }

    [Fact]
    public void ShouldIncludeToolViewModel_keeps_manual_compaction_when_tools_hidden()
    {
        var autoCompactionVm = new ChatMessageViewModel(
            CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(1000, 500, 3, null, "summary")));
        var manualCompactionVm = new ChatMessageViewModel(
            CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(
                    1000, 500, 3, null, "summary", CompactionStrategy.ManualCompact)));
        var toolVm = new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.Tool, "ToolCallId: x\nTool `read_file` succeeded."));
        var pendingApprovalVm = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("call-approval", "file_write", new Dictionary<string, string>()));
        pendingApprovalVm.MarkAwaitingApproval("""{"path":"a.txt"}""");

        Assert.False(ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: false, autoCompactionVm));
        Assert.True(ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: false, manualCompactionVm));
        Assert.False(ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: false, toolVm));
        Assert.True(ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: false, pendingApprovalVm));
        Assert.False(ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: true, toolVm));
    }

    [Fact]
    public void BuildReplayEvents_includes_pending_tool_approval_when_tools_hidden()
    {
        var pending = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("call-approval", "file_write", new Dictionary<string, string>()));
        pending.MarkAwaitingApproval("""{"path":"approval-test.txt","content":"hello"}""");

        var events = ChatEventSerializer.BuildReplayEvents([pending], showToolCalls: false);

        Assert.Contains(events, json => json.Contains("TOOL_APPROVAL_REQUEST", StringComparison.Ordinal));
        Assert.Contains(events, json => json.Contains("approval-test.txt", StringComparison.Ordinal));
        Assert.Contains(events, json => json.Contains("\"status\":\"awaiting_approval\"", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReplayEvents_includes_resolved_tool_approval()
    {
        var pending = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("call-denied", "file_write", new Dictionary<string, string>()));
        pending.MarkAwaitingApproval("""{"path":"deny.txt"}""");
        pending.ApplyToolApprovalDecision(ToolApprovalDecision.Denied);

        var events = ChatEventSerializer.BuildReplayEvents([pending], showToolCalls: true);
        var resolved = Assert.Single(events, json => json.Contains("TOOL_APPROVAL_RESOLVED", StringComparison.Ordinal));

        Assert.Contains("\"approved\":false", resolved, StringComparison.Ordinal);
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
        Assert.DoesNotContain(hidden, vm => vm.IsCompaction);
        Assert.Equal(2, hidden.Count);

        // Activity tools are folded into TURN_ACTIVITY, not kept as tool view-models.
        var shown = ChatTimelineHydrator.BuildDisplayMessages(displayMessages, showToolCalls: true);
        Assert.DoesNotContain(shown, vm => vm.IsTool && !vm.IsCompaction);
        Assert.DoesNotContain(shown, vm => vm.IsCompaction);
        Assert.Equal(2, shown.Count);
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
        Assert.Equal(0, hidden.Count(json => json.Contains("TOOL_CALL_START", StringComparison.Ordinal)));
        Assert.DoesNotContain(hidden, json => json.Contains("COMPACTION_CHECKPOINT", StringComparison.Ordinal));
        // Folded into the single turn-activity summary instead of a tool card.
        Assert.Contains(hidden, json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal));

        var shown = ChatEventSerializer.BuildReplayEvents(messages, showToolCalls: true);
        Assert.Equal(0, shown.Count(json => json.Contains("TOOL_CALL_START", StringComparison.Ordinal)));
        Assert.Contains(shown, json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal));
        Assert.Contains(shown, json => json.Contains("read_file", StringComparison.Ordinal));
        Assert.DoesNotContain(shown, json => json.Contains("COMPACTION_CHECKPOINT", StringComparison.Ordinal));
    }

    [Fact]
    public void PagedHydration_does_not_synthesize_orphan_tool_at_page_boundary()
    {
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            "working",
            toolCalls: [new AgentToolCall("cross-page-call", "read_file", new Dictionary<string, string>())]);

        var full = ChatTimelineHydrator.BuildDisplayMessages(
            [assistant],
            showToolCalls: true,
            synthesizeInterruptedToolResults: true);
        var paged = ChatTimelineHydrator.BuildDisplayMessages(
            [assistant],
            showToolCalls: true,
            synthesizeInterruptedToolResults: false);

        // Orphan tool synthesis still skipped for folded activity tools.
        Assert.DoesNotContain(full, message => message.IsTool);
        Assert.DoesNotContain(paged, message => message.IsTool);
    }

    [Fact]
    public void BuildReplayEvents_includes_running_pending_manual_compaction()
    {
        var pending = ChatMessageViewModel.CreatePendingManualCompaction();
        var events = ChatEventSerializer.BuildReplayEvents([pending], showToolCalls: false);

        Assert.Contains(events, json => json.Contains("COMPACTION_CHECKPOINT", StringComparison.Ordinal));
        Assert.Contains(events, json => json.Contains(ChatMessageViewModel.PendingManualCompactionMessageId, StringComparison.Ordinal));
        Assert.DoesNotContain(events, json => json.Contains("TOOL_CALL_START", StringComparison.Ordinal));
        Assert.DoesNotContain(events, json => json.Contains("TOOL_CALL_RESULT", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDisplayMessages_keeps_manual_compaction_hides_auto()
    {
        var auto = CompactionMessageContent.CreateCompactionMessage(
            CompactionMessageContent.CreateConversationCompact(1000, 500, 3, null, "auto"));
        var manual = CompactionMessageContent.CreateCompactionMessage(
            CompactionMessageContent.CreateConversationCompact(
                1000, 500, 3, null, "manual", CompactionStrategy.ManualCompact));
        var displayMessages = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "hello"),
            auto,
            manual,
            ChatMessage.Create(MessageRole.Assistant, "done")
        };

        var shown = ChatTimelineHydrator.BuildDisplayMessages(displayMessages, showToolCalls: false);
        Assert.DoesNotContain(shown, vm => vm.MessageId == auto.Id);
        Assert.Contains(shown, vm => vm.IsCompaction && vm.MessageId == manual.Id);
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
        var activity = Assert.Single(events, json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal));
        Assert.Contains("\"status\":\"failed\"", activity, StringComparison.Ordinal);
        Assert.DoesNotContain(events, json => json.Contains("TOOL_CALL_RESULT", StringComparison.Ordinal));
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
    public void SerializeReplayCommand_embeds_events_as_json_and_prerenders_assistant_markdown()
    {
        var assistant = new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.Assistant, "**bold**"));

        var json = ChatEventSerializer.SerializeReplayCommand([assistant]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("replay", root.GetProperty("command").GetString());
        var events = root.GetProperty("events");
        Assert.Equal(JsonValueKind.Array, events.ValueKind);
        Assert.Equal("RESET_TIMELINE", events[0].GetProperty("type").GetString());
        var assistantEvent = Assert.Single(
            events.EnumerateArray(),
            item => item.GetProperty("type").GetString() == "STATIC_ASSISTANT_HTML");
        var html = assistantEvent.GetProperty("html").GetString()!;
        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.False(assistantEvent.TryGetProperty("htmlB64", out _));
    }

    [Fact]
    public void SerializeStaticAssistantHtml_streaming_true_prerenders_markdown_with_flag()
    {
        var assistant = new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.Assistant, "## Title\n**bold**"));

        using var document = JsonDocument.Parse(
            ChatEventSerializer.SerializeStaticAssistantHtml(assistant, streaming: true));

        var root = document.RootElement;
        Assert.Equal("STATIC_ASSISTANT_HTML", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("streaming").GetBoolean());
        Assert.Contains("<h2", root.GetProperty("html").GetString()!, StringComparison.Ordinal);
        Assert.Contains("<strong>bold</strong>", root.GetProperty("html").GetString()!, StringComparison.Ordinal);
        Assert.Equal(assistant.Content, root.GetProperty("markdown").GetString());
    }

    [Fact]
    public void SerializeStaticAssistantHtml_default_is_not_streaming()
    {
        var assistant = new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.Assistant, "done"));

        using var document = JsonDocument.Parse(
            ChatEventSerializer.SerializeStaticAssistantHtml(assistant));

        Assert.False(document.RootElement.GetProperty("streaming").GetBoolean());
    }

    [Fact]
    public void SerializeResetCommand_is_a_valid_empty_reset_command()
    {
        using var document = JsonDocument.Parse(ChatEventSerializer.SerializeResetCommand());

        Assert.Equal("reset", document.RootElement.GetProperty("command").GetString());
        Assert.Empty(document.RootElement.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public void SerializePrependCommand_omits_reset_and_carries_history_availability()
    {
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "older"));

        using var document = JsonDocument.Parse(
            ChatEventSerializer.SerializePrependCommand([user], showToolCalls: false, hasOlderMessages: true));

        var root = document.RootElement;
        Assert.Equal("prepend", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("hasOlderMessages").GetBoolean());
        Assert.DoesNotContain(
            root.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "RESET_TIMELINE");
    }

    [Fact]
    public void SerializeAppendCommand_omits_reset()
    {
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "more"));

        using var document = JsonDocument.Parse(
            ChatEventSerializer.SerializeAppendCommand([user], showToolCalls: false));

        var root = document.RootElement;
        Assert.Equal("append", root.GetProperty("command").GetString());
        Assert.False(root.TryGetProperty("hasOlderMessages", out _));
        Assert.DoesNotContain(
            root.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "RESET_TIMELINE");
    }

    [Fact]
    public void SerializeEventsCommand_wraps_prebuilt_events_without_rebuilding_timeline()
    {
        var events = ChatEventSerializer.BuildReplayEvents(
            [new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "hi"))],
            includeReset: false);

        using var document = JsonDocument.Parse(
            ChatEventSerializer.SerializeEventsCommand("append", events));

        Assert.Equal("append", document.RootElement.GetProperty("command").GetString());
        Assert.Contains(
            document.RootElement.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "STATIC_ASSISTANT_HTML");
        Assert.DoesNotContain(
            document.RootElement.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "RESET_TIMELINE");
    }

    [Fact]
    public void IsToolStreamEvent_matches_tool_stream_types()
    {
        Assert.True(ChatDisplayPolicy.IsToolStreamEvent(new AgentStreamEvent.ToolCallStart("id", "tool", 0)));
        Assert.True(ChatDisplayPolicy.IsToolStreamEvent(new AgentStreamEvent.ToolCallArgs("id", "{}")));
        Assert.False(ChatDisplayPolicy.IsToolStreamEvent(new AgentStreamEvent.TextMessageContent("id", "hi")));
    }

    [Fact]
    public void Serialize_compaction_uses_checkpoint_event_not_tool_call()
    {
        var vm = new ChatMessageViewModel(
            CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(1000, 500, 3, null, "summary")));

        var json = ChatEventSerializer.SerializeCompactionCheckpoint(vm);

        Assert.Contains("COMPACTION_CHECKPOINT", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOL_CALL_START", json, StringComparison.Ordinal);
        Assert.Contains("1.0K", json, StringComparison.Ordinal);
        Assert.Contains("500", json, StringComparison.Ordinal);
        Assert.Contains("summary", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_overflow_retry_skipped_includes_localized_message()
    {
        var json = ChatEventSerializer.Serialize(
            new AgentStreamEvent.OverflowRetrySkipped(8000, 8000, "payload_not_reduced"));

        Assert.Contains("OVERFLOW_RETRY_SKIPPED", json, StringComparison.Ordinal);
        Assert.Contains("payload_not_reduced", json, StringComparison.Ordinal);
        Assert.Contains("\"failedTokens\":8000", json, StringComparison.Ordinal);
    }
}
