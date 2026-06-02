using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

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
        var callbacks = ui.BuildCallbacks(new LiveAgentSession(compactedSession));
        await callbacks.OnMessage!(compactionMessage);

        Assert.Equal(4, ui.Messages.Count);
        Assert.Equal(3, ui.Messages.Count(message => !message.IsCompaction));
        Assert.Single(ui.Messages, message => message.IsCompaction);
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
