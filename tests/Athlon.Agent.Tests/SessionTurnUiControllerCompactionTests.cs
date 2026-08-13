using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class SessionTurnUiControllerCompactionTests
{
    [Fact]
    public async Task Compaction_DoesNotRemoveExistingDisplayMessages()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);

        await dispatcher.InvokeAsync(() =>
        {
            ui.Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "one")));
            ui.Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.Assistant, "two")));
            ui.Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "three")));
        });

        var compactedSession = AgentSession.Create("test").WithMessages(
        [
            CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(1000, 500, 3, null, "summary")),
            ChatMessage.Create(MessageRole.User, "three"),
            ChatMessage.Create(MessageRole.Assistant, "four")
        ]);

        var compactionMessage = compactedSession.Messages[0];
        ui.SetDisplayed(true);
        var callbacks = ui.BuildCallbacks(new LiveAgentSession(compactedSession));
        await callbacks.OnStreamEvent!(new AgentStreamEvent.ChatMessageAppended(compactionMessage));

        Assert.Equal(4, ui.Messages.Count);
        Assert.Equal(3, ui.Messages.Count(message => !message.IsCompaction));
        Assert.Single(ui.Messages, message => message.IsCompaction);
    }

    [Fact]
    public async Task BeginManualCompactionBubble_AddsRunningCompactionCard()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);

        await dispatcher.InvokeAsync(() =>
        {
            ui.Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "one")));
            ui.BeginManualCompactionBubble();
        });

        await dispatcher.InvokeAsync(() =>
        {
            Assert.Equal(2, ui.Messages.Count);
            var pending = Assert.Single(ui.Messages, message => message.IsCompaction);
            Assert.True(pending.IsToolRunning);
            Assert.Equal(ChatMessageViewModel.PendingManualCompactionMessageId, pending.MessageId);
            Assert.Equal(Athlon.Agent.App.Resources.Strings.Get("Chat_CompactionRunning"), pending.CompactionCardTitle);
        });
    }

    [Fact]
    public async Task CancelManualCompactionBubble_RemovesPendingCompactionCard()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);

        await dispatcher.InvokeAsync(() =>
        {
            ui.BeginManualCompactionBubble();
            ui.CancelManualCompactionBubble();
        });

        await dispatcher.InvokeAsync(() =>
        {
            Assert.Empty(ui.Messages);
        });
    }

    [Fact]
    public async Task DismissManualCompactionBubble_RemovesPendingWithoutCancelledState()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);

        await dispatcher.InvokeAsync(() =>
        {
            ui.BeginManualCompactionBubble();
            var pending = Assert.Single(ui.Messages);
            Assert.Equal(ToolCallDisplayStatus.Running, pending.ToolCallStatus);
            ui.DismissManualCompactionBubble();
        });

        await dispatcher.InvokeAsync(() =>
        {
            Assert.Empty(ui.Messages);
        });
    }

    [Fact]
    public async Task OverflowRetrySkipped_InvokesCallbackAndKeepsTimeline()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        var overflowInvoked = false;
        ui.OnOverflowRetrySkipped = () => overflowInvoked = true;

        await dispatcher.InvokeAsync(() =>
            ui.Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "hello"))));

        ui.SetDisplayed(true);
        var callbacks = ui.BuildCallbacks();
        await callbacks.OnStreamEvent!(
            new AgentStreamEvent.OverflowRetrySkipped(8000, 8000, "payload_not_reduced"));

        Assert.True(overflowInvoked);
        await dispatcher.InvokeAsync(() =>
        {
            Assert.Single(ui.Messages);
            Assert.Equal("hello", ui.Messages[0].Content);
        });
    }

    [Fact]
    public async Task ContextBudgetUpdated_InvokesCallbackWithoutAddingMessages()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ContextBudgetSnapshot? received = null;
        ContextPressureLevel? pressure = null;
        ui.OnContextBudgetUpdated = (budget, level) =>
        {
            received = budget;
            pressure = level;
        };

        ui.SetDisplayed(true);
        var callbacks = ui.BuildCallbacks();
        var snapshot = new ContextBudgetSnapshot(100_000, 8192, 1000, 90_000, 2000, 0.02, 400, 200, 400);
        await callbacks.OnStreamEvent!(
            new AgentStreamEvent.ContextBudgetUpdated(snapshot, ContextPressureLevel.Elevated));

        Assert.Same(snapshot, received);
        Assert.Equal(ContextPressureLevel.Elevated, pressure);
        await dispatcher.InvokeAsync(() => Assert.Empty(ui.Messages));
    }

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
