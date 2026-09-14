using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests.Plan;

/// <summary>
/// The approved plan travels as a regular user message so it is persisted and replayed like any
/// other turn. It must never surface as a user bubble, otherwise the hidden control message would
/// look like something the user typed.
/// </summary>
public sealed class ApprovedPlanPromptTests
{
    private const string PlanMarkdown = """
        # Ship it

        ## Steps
        1. Do the thing

        ## Acceptance
        - [ ] Works
        """;

    [Fact]
    public void BuildUserMessage_carries_the_marker_and_the_plan()
    {
        var content = ApprovedPlanPrompt.BuildUserMessage(PlanMarkdown);

        Assert.Contains(ApprovedPlanPrompt.Marker, content, StringComparison.Ordinal);
        Assert.Contains("# Ship it", content, StringComparison.Ordinal);
        Assert.Contains("Do the thing", content, StringComparison.Ordinal);
    }

    [Fact]
    public void IsApprovedPlanMessage_only_matches_the_marked_user_message()
    {
        var message = ChatMessage.Create(
            MessageRole.User,
            ApprovedPlanPrompt.BuildUserMessage(PlanMarkdown));

        Assert.True(ApprovedPlanPrompt.IsApprovedPlanMessage(message));
        Assert.False(ApprovedPlanPrompt.IsApprovedPlanMessage(
            ChatMessage.Create(MessageRole.User, "please revise the plan")));
        // A marker in an assistant reply is not a user control message.
        Assert.False(ApprovedPlanPrompt.IsApprovedPlanMessage(
            ChatMessage.Create(MessageRole.Assistant, ApprovedPlanPrompt.Marker)));
    }

    [Fact]
    public void ChatMessageViewModel_hides_the_approved_plan_message()
    {
        var message = ChatMessage.Create(
            MessageRole.User,
            ApprovedPlanPrompt.BuildUserMessage(PlanMarkdown));

        var viewModel = new ChatMessageViewModel(message);

        Assert.True(viewModel.IsHiddenPlaceholder);
    }

    [Fact]
    public void Hydrator_hides_the_approved_plan_message()
    {
        var message = ChatMessage.Create(
            MessageRole.User,
            ApprovedPlanPrompt.BuildUserMessage(PlanMarkdown));

        Assert.True(ChatTimelineHydrator.ShouldHideMessageFromChat(message));
        Assert.False(ChatTimelineHydrator.ShouldHideMessageFromChat(
            ChatMessage.Create(MessageRole.User, "ordinary user text")));
    }

    [Fact]
    public void BuildUserMessage_tolerates_missing_markdown()
    {
        var content = ApprovedPlanPrompt.BuildUserMessage(null!);

        Assert.Contains(ApprovedPlanPrompt.Marker, content, StringComparison.Ordinal);
    }
}
