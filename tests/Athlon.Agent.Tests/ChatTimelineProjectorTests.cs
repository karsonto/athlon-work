using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ChatTimelineProjectorTests
{
    [Fact]
    public void HighFidelity_replay_folds_activity_tools_into_turn_activity()
    {
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "find it"));
        var grep = CreateToolMessage("grep", "call-1", "Tool `grep`\nSummary: 3 matches");
        var read = CreateToolMessage("read_file", "call-2", "Tool `read_file`\nSummary: ok");
        var assistant = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "done"));

        var liveFold = ChatEventSerializer.BuildReplayEvents(
            [user, grep, read, assistant],
            showToolCalls: true,
            includeReset: false,
            mode: TimelineProjectionMode.LiveFold);
        var highFidelity = ChatEventSerializer.BuildReplayEvents(
            [user, grep, read, assistant],
            showToolCalls: true,
            includeReset: false,
            mode: TimelineProjectionMode.HighFidelity);

        Assert.Equal(0, CountToolStarts(liveFold));
        Assert.Equal(0, CountToolStarts(highFidelity));

        var activity = Assert.Single(
            highFidelity,
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
            highFidelity,
            json => json.Contains("\"STATIC_ASSISTANT_HTML\"", StringComparison.Ordinal)
                && json.Contains("grep", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveFold_keeps_computer_use_tools_as_individual_cards()
    {
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "look"));
        var observe = CreateToolMessage("computer_observe", "cu-1", "Tool `computer_observe`\nSummary: frame");
        var assistant = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "clicked"));

        var events = ChatEventSerializer.BuildReplayEvents(
            [user, observe, assistant],
            showToolCalls: true,
            includeReset: false,
            mode: TimelineProjectionMode.LiveFold);

        Assert.Equal(1, CountToolStarts(events));
    }

    [Fact]
    public void HighFidelity_keeps_computer_use_tools_as_individual_cards()
    {
        var user = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "look"));
        var observe = CreateToolMessage("computer_observe", "cu-1", "Tool `computer_observe`\nSummary: frame");
        var assistant = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "clicked"));

        var events = ChatEventSerializer.BuildReplayEvents(
            [user, observe, assistant],
            showToolCalls: true,
            includeReset: false,
            mode: TimelineProjectionMode.HighFidelity);

        Assert.Equal(1, CountToolStarts(events));
        Assert.DoesNotContain(events, json => json.Contains("\"TURN_ACTIVITY\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldEmitToolCard_returns_false_for_activity_tools_in_both_modes()
    {
        var tool = CreateToolMessage("grep", "call-1", "Tool `grep`\nSummary: ok");

        Assert.False(ChatTimelineProjector.ShouldEmitToolCard(
            showToolCalls: true,
            TimelineProjectionMode.LiveFold,
            tool));
        Assert.False(ChatTimelineProjector.ShouldEmitToolCard(
            showToolCalls: true,
            TimelineProjectionMode.HighFidelity,
            tool));
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
