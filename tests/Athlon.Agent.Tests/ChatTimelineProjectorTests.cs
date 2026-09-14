using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ChatTimelineProjectorTests
{
    [Fact]
    public void Replay_folds_activity_tools_into_turn_activity()
    {
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "find it"));
        var grep = CreateToolMessage("grep", "call-1", "Tool `grep`\nSummary: 3 matches");
        var read = CreateToolMessage("read_file", "call-2", "Tool `read_file`\nSummary: ok");
        var assistant = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "done"));

        var events = ChatEventSerializer.BuildReplayEvents(
            [user, grep, read, assistant],
            showToolCalls: true,
            includeReset: false);

        Assert.Equal(0, CountToolStarts(events));

        var activity = Assert.Single(
            events,
            json => json.Contains("\"TURN_ACTIVITY\"", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(activity);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.All(
            items.EnumerateArray(),
            item =>
            {
                Assert.True(item.TryGetProperty("messageId", out var messageId));
                Assert.False(string.IsNullOrWhiteSpace(messageId.GetString()));
                Assert.True(item.TryGetProperty("toolCallId", out var toolCallId));
                Assert.False(string.IsNullOrWhiteSpace(toolCallId.GetString()));
            });
        Assert.DoesNotContain(
            events,
            json => json.Contains("\"STATIC_ASSISTANT_HTML\"", StringComparison.Ordinal)
                && json.Contains("grep", StringComparison.Ordinal));
    }

    [Fact]
    public void Replay_keeps_computer_use_tools_as_individual_cards()
    {
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "look"));
        var observe = CreateToolMessage("computer_observe", "cu-1", "Tool `computer_observe`\nSummary: frame");
        var assistant = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "clicked"));

        var events = ChatEventSerializer.BuildReplayEvents(
            [user, observe, assistant],
            showToolCalls: true,
            includeReset: false);

        Assert.Equal(1, CountToolStarts(events));
        Assert.DoesNotContain(events, json => json.Contains("\"TURN_ACTIVITY\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldEmitToolCard_returns_false_for_activity_tools()
    {
        var tool = CreateToolMessage("grep", "call-1", "Tool `grep`\nSummary: ok");

        Assert.False(ChatTimelineProjector.ShouldEmitToolCard(showToolCalls: true, tool));
    }

    [Fact]
    public void BuildSegments_keeps_tool_and_assistant_order_within_a_turn()
    {
        // The model replied between two tool calls; the projection must keep that interleaving so
        // live and replay agree on each bubble's seq. Previously tools were hoisted ahead of text.
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "go"));
        var tool1 = CreateToolMessage("computer_observe", "cu-1", "Tool `computer_observe`\nSummary: frame 1");
        var assistant = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "next"));
        var tool2 = CreateToolMessage("computer_observe", "cu-2", "Tool `computer_observe`\nSummary: frame 2");

        var segments = ChatTimelineProjector.BuildSegments(
            [user, tool1, assistant, tool2],
            showToolCalls: true);

        var content = Assert.Single(segments, segment => segment.Blocks.Count > 0)
            .Blocks
            .OfType<ChatTimelineProjector.ContentBlock>()
            .Select(block => block.Message);
        Assert.Equal(["cu-1", "next", "cu-2"], content.Select(ResolveKey));
    }

    [Fact]
    public void BuildSegments_interleaves_activity_folds_between_assistant_bubbles()
    {
        // A thought, a tool, an assistant reply, another thought: the folds must sit between the
        // replies rather than being hoisted to a single per-turn card.
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "go"));
        var grep = CreateToolMessage("grep_files", "call-1", "Tool `grep_files`\nSummary: 3 matches");
        var first = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "first"));
        var read = CreateToolMessage("file_read", "call-2", "Tool `file_read`\nSummary: ok");
        var second = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "second"));

        var turn = Assert.Single(
            ChatTimelineProjector.BuildSegments(
                [user, grep, first, read, second],
                showToolCalls: false),
            segment => segment.Blocks.Count > 0);

        var kinds = turn.Blocks
            .Select(block => block switch
            {
                ChatTimelineProjector.ActivityBlock => "fold",
                ChatTimelineProjector.ContentBlock { Message: var message } => ResolveKey(message),
                _ => "?"
            });
        Assert.Equal(["fold", "first", "fold", "second"], kinds);
    }

    [Fact]
    public void BuildSegments_keeps_a_reasoning_only_fold()
    {
        // A fold with reasoning but no tools still occupies a block; otherwise the thought would be
        // dropped from the timeline entirely.
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "go"));
        var thinking = new ChatMessageViewModel(ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            reasoningContent: "weighing options"));
        var reply = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "done"));

        var turn = Assert.Single(
            ChatTimelineProjector.BuildSegments([user, thinking, reply], showToolCalls: true),
            segment => segment.Blocks.Count > 0);

        var fold = Assert.IsType<ChatTimelineProjector.ActivityBlock>(turn.Blocks[0]);
        Assert.Contains(fold.Messages, message => message.ReasoningContent == "weighing options");
        var content = Assert.IsType<ChatTimelineProjector.ContentBlock>(turn.Blocks[1]);
        Assert.Equal("done", content.Message.Content);
    }

    [Fact]
    public void BuildReplayEvents_assigns_ascending_seq_within_a_turn()
    {
        var userMessage = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "go"));
        var tool = CreateToolMessage("computer_observe", "cu-1", "Tool `computer_observe`\nSummary: frame");
        var assistant = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "done"));

        var events = ChatEventSerializer.BuildReplayEvents(
            [userMessage, tool, assistant],
            showToolCalls: true,
            includeReset: false);

        var seqs = events
            .Select(ReadSeq)
            .Where(seq => seq is not null)
            .Select(seq => seq!.Value)
            .ToList();
        Assert.NotEmpty(seqs);
        Assert.Equal(seqs.OrderBy(seq => seq), seqs);
    }

    [Fact]
    public void BuildReplayEvents_anchors_activity_and_files_to_the_turn_user_message()
    {
        var user = ChatMessage.Create(MessageRole.User, "explore");
        var read = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: c1",
                "Tool `file_read` succeeded.",
                "",
                "Arguments: path = a.ts",
                "Summary: Read a.ts",
                ""));

        var events = ChatEventSerializer.BuildReplayEvents(
            [new ChatMessageViewModel(user)],
            showToolCalls: false,
            includeReset: false,
            activitySourceMessages: [user, read]);

        var activity = Assert.Single(events, json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(activity);
        Assert.Equal("activity:" + user.Id + ":0", doc.RootElement.GetProperty("entryId").GetString());
    }

    private static string ResolveKey(ChatMessageViewModel message) =>
        message.IsTool ? message.ToolCallId ?? string.Empty : message.Content;

    private static long? ReadSeq(string eventJson)
    {
        using var doc = JsonDocument.Parse(eventJson);
        return doc.RootElement.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number
            ? seq.GetInt64()
            : null;
    }

    private static int CountToolStarts(IReadOnlyList<string> events) =>
        events.Count(json => json.Contains("\"TOOL_CALL_START\"", StringComparison.Ordinal));

    private static ChatMessageViewModel CreateToolMessage(string toolName, string toolCallId, string content)
    {
        var body = string.Join(
            Environment.NewLine,
            $"ToolCallId: {toolCallId}",
            $"Tool `{toolName}` succeeded.",
            "",
            content);
        return new ChatMessageViewModel(ChatMessage.Create(MessageRole.Tool, body));
    }
}
