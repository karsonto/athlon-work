using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionTurnUiControllerDisplayTests
{
    [Fact]
    public async Task HiddenSession_buffers_text_delta_without_adding_messages()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(true);

        await dispatcher.InvokeAsync(() => ui.Messages.Add(
            new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "hello"))));

        var initialCount = await dispatcher.InvokeAsync(() => ui.Messages.Count);
        ui.SetDisplayed(false);

        var callbacks = ui.BuildCallbacks();
        await callbacks.OnAssistantTextDelta!("world");

        var countWhileHidden = await dispatcher.InvokeAsync(() => ui.Messages.Count);
        Assert.Equal(initialCount, countWhileHidden);

        ui.SetDisplayed(true);

        var assistant = await dispatcher.InvokeAsync(() =>
            ui.Messages.LastOrDefault(message => !message.IsUser && !message.IsTool));

        Assert.NotNull(assistant);
        Assert.Contains("world", assistant!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HiddenSession_finalize_turn_applies_persisted_assistant_message()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(false);

        var session = AgentSession.Create("test");
        var persisted =
        [
            ChatMessage.Create(MessageRole.User, "question"),
            ChatMessage.Create(MessageRole.Assistant, "answer")
        ];

        await dispatcher.InvokeAsync(() =>
            ui.FinalizeTurn(session, persisted, cancelled: false, timedOut: false, turnTimeoutMinutes: 30));

        var messages = await dispatcher.InvokeAsync(() => ui.Messages.ToList());
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, message => message.Content == "answer");
    }

    [Fact]
    public async Task CaptureEndSnapshot_includes_buffered_tokens_when_hidden()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(false);

        var callbacks = ui.BuildCallbacks();
        await callbacks.OnAssistantTextDelta!("buffered ");

        var session = AgentSession.Create("test");
        var snapshot = await dispatcher.InvokeAsync(() =>
            ui.CaptureEndSnapshot(session, wasCancelled: false, timedOut: false, errorMessage: null));

        Assert.Equal("buffered ", snapshot.AssistantContent);
    }

    [Fact]
    public async Task DisplayedSession_text_delta_buffers_before_timer_flush()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(true);

        var callbacks = ui.BuildCallbacks();
        await callbacks.OnAssistantTextDelta!("hello ");
        await callbacks.OnAssistantTextDelta!("world");

        var countBeforeFlush = await dispatcher.InvokeAsync(() => ui.Messages.Count);
        Assert.Equal(0, countBeforeFlush);

        ui.SetDisplayed(false);
        ui.SetDisplayed(true);

        var assistant = await dispatcher.InvokeAsync(() =>
            ui.Messages.LastOrDefault(message => !message.IsUser && !message.IsTool));

        Assert.NotNull(assistant);
        Assert.Contains("hello world", assistant!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisplayedSession_tool_delta_batches_before_timer_flush()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(true);

        var callbacks = ui.BuildCallbacks();
        await callbacks.OnAssistantToolCallDelta!(new StreamingToolCallDelta(0, "call-1", "read_file", "{\"path\":"));
        await callbacks.OnAssistantToolCallDelta!(new StreamingToolCallDelta(0, "call-1", "read_file", "{\"path\":\"/tmp\"}"));

        var countBeforeFlush = await dispatcher.InvokeAsync(() => ui.Messages.Count);
        Assert.Equal(0, countBeforeFlush);

        ui.SetDisplayed(false);
        ui.SetDisplayed(true);

        var tool = await dispatcher.InvokeAsync(() =>
            ui.Messages.LastOrDefault(message => message.IsTool));

        Assert.NotNull(tool);
        Assert.Equal(ToolCallDisplayStatus.Preparing, tool!.ToolCallStatus);
        Assert.Contains("/tmp", tool.ToolArgumentsText, StringComparison.Ordinal);
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
