using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ChatTimelineProjectorTests
{
    [Fact]
    public void HighFidelity_replay_emits_individual_activity_tool_cards()
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
        Assert.Equal(2, CountToolStarts(highFidelity));
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
    public void ShouldEmitToolCard_differs_by_mode_for_activity_tools()
    {
        var tool = CreateToolMessage("grep", "call-1", "Tool `grep`\nSummary: ok");

        Assert.False(ChatTimelineProjector.ShouldEmitToolCard(
            showToolCalls: true,
            TimelineProjectionMode.LiveFold,
            tool));
        Assert.True(ChatTimelineProjector.ShouldEmitToolCard(
            showToolCalls: true,
            TimelineProjectionMode.HighFidelity,
            tool));
    }

    private static int CountToolStarts(IReadOnlyList<string> events) =>
        events.Count(json => json.Contains("\"TOOL_CALL_START\"", StringComparison.Ordinal));

    private static ChatMessageViewModel CreateToolMessage(string toolName, string toolCallId, string content)
    {
        var body = $"Tool `{toolName}` (id: `{toolCallId}`)\n{content}";
        return new ChatMessageViewModel(ChatMessage.Create(MessageRole.Tool, body));
    }
}
