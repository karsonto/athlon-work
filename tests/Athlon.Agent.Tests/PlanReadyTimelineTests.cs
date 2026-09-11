using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

/// <summary>
/// The plan-ready card is the one timeline entry that is not derived from the transcript:
/// <c>publish_plan</c> renders as a plan card, not a tool card, so a full replay used to drop it.
/// These tests pin the replacement contract: the projector reports the publishing turn, the replay
/// emits the card onto that turn's plan slot, and the card no longer sorts by arrival order.
/// </summary>
public sealed class PlanReadyTimelineTests
{
    private const string UserMessageId = "user-plan-1";

    [Fact]
    public void Publish_plan_is_not_projected_as_tool_or_activity()
    {
        var messages = TranscriptWithPlanPublish();

        var segments = ChatTimelineProjector.BuildSegments(messages, showToolCalls: true);

        var publishing = Assert.Single(segments, segment => segment.HasPlanPublish);
        Assert.Equal(UserMessageId, publishing.TurnAnchorId);
        // publish_plan must not fold: doing so would make the turn "have activity" and relegate the
        // final assistant reply out of the content bubbles.
        Assert.DoesNotContain(
            publishing.ActivitySegment,
            message => PlanTimelinePolicy.IsPublishPlanTool(message.ToolName));
        Assert.DoesNotContain(
            publishing.ContentMessages,
            message => PlanTimelinePolicy.IsPublishPlanTool(message.ToolName));
        // The final reply survives as a content bubble.
        Assert.Contains(publishing.ContentMessages, message => message.Content == "Plan is ready.");
    }

    [Fact]
    public void Publish_plan_turn_keeps_the_final_assistant_reply_as_a_bubble()
    {
        var messages = TranscriptWithPlanPublish();

        var projected = ChatTimelineProjector.BuildSegments(messages, showToolCalls: true);
        var publishing = Assert.Single(projected, segment => segment.HasPlanPublish);
        Assert.Contains(publishing.ContentMessages, message => message.Content == "Plan is ready.");
    }

    [Fact]
    public void Replay_emits_the_plan_card_on_the_publishing_turn_slot()
    {
        var messages = TranscriptWithPlanPublish();
        var run = BuildRun();

        var events = ChatEventSerializer.BuildReplayEvents(
            messages,
            showToolCalls: true,
            planRun: run);

        var planReady = Assert.Single(events, json => json.Contains("PLAN_READY", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(planReady);
        var root = document.RootElement;

        Assert.Equal(run.Id, root.GetProperty("runId").GetString());
        // The projector numbers a user message and its response as consecutive bands, so the first
        // turn's publish response is band 1. The card lands on that band's plan slot, which is after
        // the reply (Content(1, 0)) and before the next user message (User(2)).
        Assert.Equal(TimelineOrderPolicy.Plan(1), root.GetProperty("seq").GetInt64());
        Assert.True(TimelineOrderPolicy.Plan(1) > TimelineOrderPolicy.Content(1, 0));
        Assert.True(TimelineOrderPolicy.Plan(1) < TimelineOrderPolicy.User(2));
    }

    [Fact]
    public void Replay_without_a_plan_run_emits_no_plan_card()
    {
        var messages = TranscriptWithPlanPublish();

        var events = ChatEventSerializer.BuildReplayEvents(messages, showToolCalls: true);

        Assert.DoesNotContain(events, json => json.Contains("PLAN_READY", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_card_slot_sorts_after_the_turn_but_before_the_next_turn()
    {
        Assert.True(TimelineOrderPolicy.Plan(1) > TimelineOrderPolicy.Content(1, 0));
        Assert.True(TimelineOrderPolicy.Plan(1) > TimelineOrderPolicy.Files(1));
        Assert.True(TimelineOrderPolicy.Plan(1) < TimelineOrderPolicy.User(2));
    }

    [Fact]
    public void SerializePlanReady_includes_seq_and_markdown()
    {
        var run = BuildRun();

        var json = ChatEventSerializer.SerializePlanReady(run, TimelineOrderPolicy.Plan(2));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("PLAN_READY", root.GetProperty("type").GetString());
        Assert.Equal(TimelineOrderPolicy.Plan(2), root.GetProperty("seq").GetInt64());
        Assert.Equal("# Ship it", root.GetProperty("markdown").GetString());
        Assert.False(root.GetProperty("built").GetBoolean());
    }

    [Fact]
    public void SerializePlanCleared_targets_the_run()
    {
        var json = ChatEventSerializer.SerializePlanCleared("run-42");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("PLAN_CLEARED", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("run-42", document.RootElement.GetProperty("runId").GetString());
    }

    private static List<ChatMessageViewModel> TranscriptWithPlanPublish()
    {
        var user = ChatMessage.CreateWithId(UserMessageId, MessageRole.User, "draft a plan");
        var publish = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-plan-1",
                "Tool `publish_plan` succeeded.",
                "",
                "Arguments: title = Ship it",
                "Summary: Plan published"));
        var assistant = ChatMessage.Create(MessageRole.Assistant, "Plan is ready.");

        return [new ChatMessageViewModel(user), new ChatMessageViewModel(publish), new ChatMessageViewModel(assistant)];
    }

    private static PlanRun BuildRun() => new()
    {
        Id = "run-plan-1",
        SessionId = "session-1",
        Phase = PlanPhase.AwaitConfirm,
        Status = PlanRunStatuses.AwaitingConfirmation,
        Title = "Ship it",
        PlanMarkdown = "# Ship it"
    };
}
