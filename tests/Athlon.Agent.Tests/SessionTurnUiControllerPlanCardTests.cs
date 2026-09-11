using System.Text.Json;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

/// <summary>
/// The plan-ready card is published by the plan store, not derived from the transcript, so the
/// controller has to remember the active run and re-emit its card on the publishing turn's slot
/// after every full replay. Otherwise the turn-end authoritative replay resets the timeline and the
/// card silently disappears — which is the regression these tests pin.
/// </summary>
[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class SessionTurnUiControllerPlanCardTests
{
    [Fact]
    public async Task ShowPlanReady_places_the_card_on_the_publishing_turn_slot()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        var posted = new List<string>();
        ui.PlanTimelineEventObserver = posted.Add;
        ui.SetDisplayed(true);

        await dispatcher.InvokeAsync(() => ui.HydrateDisplay(
            AgentSession.Create("plan-card"),
            TranscriptWithPlanPublish()));

        ui.ShowPlanReady(BuildRun());

        var planEvent = Assert.Single(posted);
        using var document = JsonDocument.Parse(planEvent);
        var root = document.RootElement;
        Assert.Equal("PLAN_READY", root.GetProperty("type").GetString());
        // User message (band 0) then the turn's response (band 1); the card owns band 1's plan slot.
        Assert.Equal(TimelineOrderPolicy.Plan(1), root.GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task The_controller_remembers_the_run_across_replays()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(true);

        await dispatcher.InvokeAsync(() => ui.HydrateDisplay(
            AgentSession.Create("plan-card-replay"),
            TranscriptWithPlanPublish()));

        var run = BuildRun();
        ui.ShowPlanReady(run);
        Assert.Same(run, ui.VisiblePlanRun);

        // A full replay is rebuilt from the transcript, which has no publish_plan record. The
        // controller must still hand the remembered run to the renderer.
        await dispatcher.InvokeAsync(() => ui.HydrateDisplay(
            AgentSession.Create("plan-card-replay"),
            TranscriptWithPlanPublish()));
        Assert.Same(run, ui.VisiblePlanRun);
    }

    [Fact]
    public async Task ClearPlanReady_drops_the_card_and_posts_a_clear_event()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        var posted = new List<string>();
        ui.PlanTimelineEventObserver = posted.Add;
        ui.SetDisplayed(true);

        await dispatcher.InvokeAsync(() => ui.HydrateDisplay(
            AgentSession.Create("plan-card-clear"),
            TranscriptWithPlanPublish()));

        var run = BuildRun();
        ui.ShowPlanReady(run);
        posted.Clear();

        ui.ClearPlanReady();

        Assert.Null(ui.VisiblePlanRun);
        var cleared = Assert.Single(posted);
        using var document = JsonDocument.Parse(cleared);
        Assert.Equal("PLAN_CLEARED", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(run.Id, document.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task Hiding_the_session_drops_the_plan_run_so_it_cannot_leak_into_the_next_one()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(true);

        await dispatcher.InvokeAsync(() => ui.HydrateDisplay(
            AgentSession.Create("plan-card-switch"),
            TranscriptWithPlanPublish()));

        ui.ShowPlanReady(BuildRun());
        Assert.NotNull(ui.VisiblePlanRun);

        // The shared WebChatView is reused for the next session, so the hidden controller must stop
        // handing its run to a replay that now belongs to a different session.
        ui.SetDisplayed(false);
        Assert.Null(ui.VisiblePlanRun);
    }

    private static List<ChatMessage> TranscriptWithPlanPublish()
    {
        var user = ChatMessage.CreateWithId("user-plan-1", MessageRole.User, "draft a plan");
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
        return [user, publish, assistant];
    }

    private static PlanRun BuildRun() => new()
    {
        Id = "run-plan-1",
        SessionId = "session-plan-1",
        Phase = PlanPhase.AwaitConfirm,
        Status = PlanRunStatuses.AwaitingConfirmation,
        Title = "Ship it",
        PlanMarkdown = "# Ship it"
    };

    private static Task<Dispatcher> StartStaDispatcherAsync()
    {
        var tcs = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            tcs.SetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
