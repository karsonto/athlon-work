using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class SessionTurnUiControllerDisplayTests
{
    private const string MessageId1 = "assistant-msg-1";
    private const string MessageId2 = "assistant-msg-2";

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
        await EmitText(callbacks, MessageId1, "world");

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
        IReadOnlyList<ChatMessage> persisted =
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
    public async Task HiddenSession_FinalizeTurn_does_not_require_ChatView_or_scroll()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var scrollCount = 0;
        var reloadCount = 0;
        var ui = new SessionTurnUiController(
            dispatcher,
            requestScrollImmediate: () => Interlocked.Increment(ref scrollCount));
        ui.ReloadChatViewOverride = () =>
        {
            Interlocked.Increment(ref reloadCount);
            return Task.CompletedTask;
        };
        ui.SetDisplayed(false);

        Assert.Null(ui.ChatView);

        var session = AgentSession.Create("test");
        IReadOnlyList<ChatMessage> persisted =
        [
            ChatMessage.Create(MessageRole.User, "question"),
            ChatMessage.Create(MessageRole.Assistant, "answer")
        ];

        await dispatcher.InvokeAsync(() =>
            ui.FinalizeTurn(session, persisted, cancelled: false, timedOut: false, turnTimeoutMinutes: 30));

        var messages = await dispatcher.InvokeAsync(() => ui.Messages.ToList());
        Assert.Equal(2, messages.Count);
        Assert.Equal(0, Volatile.Read(ref scrollCount));
        Assert.Equal(0, Volatile.Read(ref reloadCount));
        Assert.Equal(0, ui.SyncChatViewGeneration);
    }

    [Fact]
    public async Task HiddenSession_does_not_sync_chat_view_until_displayed()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var reloadCount = 0;
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () =>
        {
            Interlocked.Increment(ref reloadCount);
            return Task.CompletedTask;
        };
        ui.SetDisplayed(false);

        await dispatcher.InvokeAsync(() =>
            ui.Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "hello"))));

        Assert.Equal(0, ui.SyncChatViewGeneration);
        Assert.Equal(0, Volatile.Read(ref reloadCount));

        ui.SetDisplayed(true);
        await dispatcher.InvokeAsync(() => ui.SyncChatViewGeneration); // pump UI after SetDisplayed
        await ui.ReloadChatViewAsync();

        Assert.Equal(1, Volatile.Read(ref reloadCount));
    }

    [Fact]
    public async Task CaptureEndSnapshot_includes_buffered_tokens_when_hidden()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(false);

        var callbacks = ui.BuildCallbacks();
        await EmitText(callbacks, MessageId1, "buffered ");

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
        await EmitText(callbacks, MessageId1, "hello ");
        await EmitText(callbacks, MessageId1, "world");

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
    public async Task HiddenSession_tool_events_flush_when_displayed()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetShowToolCalls(true);
        ui.SetDisplayed(false);

        var callbacks = ui.BuildCallbacks();
        await EmitToolStart(callbacks, "call-1", "read_file", 0);
        await EmitToolArgs(callbacks, "call-1", "{\"path\":");
        await EmitToolArgs(callbacks, "call-1", "{\"path\":\"/tmp\"}");

        var countWhileHidden = await dispatcher.InvokeAsync(() => ui.Messages.Count);
        Assert.Equal(0, countWhileHidden);

        ui.SetDisplayed(true);

        var tool = await dispatcher.InvokeAsync(() =>
            ui.Messages.LastOrDefault(message => message.IsTool));

        Assert.NotNull(tool);
        Assert.Equal(ToolCallDisplayStatus.Preparing, tool!.ToolCallStatus);
        Assert.Contains("/tmp", tool.ToolArgumentsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interleaved_text_and_tool_deltas_use_separate_assistant_bubbles_after_tool()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetShowToolCalls(true);
        ui.SetDisplayed(true);

        var callbacks = ui.BuildCallbacks();
        await EmitText(callbacks, MessageId1, "hello");
        ui.SetDisplayed(false);
        ui.SetDisplayed(true);

        await EmitTextEnd(callbacks, MessageId1);
        await EmitToolStart(callbacks, "call-1", "read_file", 0);
        await EmitToolArgs(callbacks, "call-1", "{}");
        ui.SetDisplayed(false);
        ui.SetDisplayed(true);

        await EmitText(callbacks, MessageId2, " world");
        ui.SetDisplayed(false);
        ui.SetDisplayed(true);

        var assistants = await dispatcher.InvokeAsync(() =>
            ui.Messages.Where(message => !message.IsUser && !message.IsTool).ToList());

        Assert.Equal(2, assistants.Count);
        Assert.Contains("hello", assistants[0].Content, StringComparison.Ordinal);
        Assert.Contains(" world", assistants[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_started_then_next_text_uses_new_assistant_bubble()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetShowToolCalls(true);
        ui.SetDisplayed(true);

        var callbacks = ui.BuildCallbacks();
        await EmitText(callbacks, MessageId1, "before tool");
        await EmitTextEnd(callbacks, MessageId1);
        await EmitToolStart(callbacks, "call-1", "read_file", 0);
        await EmitToolArgs(callbacks, "call-1", "{}");
        await EmitToolEnd(callbacks, "call-1");
        await EmitText(callbacks, MessageId2, "after tool");

        ui.SetDisplayed(false);
        ui.SetDisplayed(true);

        var assistants = await dispatcher.InvokeAsync(() =>
            ui.Messages.Where(message => !message.IsUser && !message.IsTool).ToList());

        Assert.Equal(2, assistants.Count);
        Assert.Contains("before tool", assistants[0].Content, StringComparison.Ordinal);
        Assert.Contains("after tool", assistants[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCallStart_does_not_add_message_when_show_tool_calls_disabled()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetShowToolCalls(false);
        ui.SetDisplayed(true);

        var callbacks = ui.BuildCallbacks();
        await EmitToolStart(callbacks, "call-1", "read_file", 0);
        await EmitToolArgs(callbacks, "call-1", "{\"path\":\"/tmp\"}");
        await EmitToolEnd(callbacks, "call-1");

        var count = await dispatcher.InvokeAsync(() => ui.Messages.Count);
        Assert.Equal(0, count);
        var tool = await dispatcher.InvokeAsync(() =>
            ui.Messages.LastOrDefault(message => message.IsTool && !message.IsCompaction));
        Assert.Null(tool);
    }

    private static Task EmitText(AgentTurnCallbacks callbacks, string messageId, string delta) =>
        callbacks.OnStreamEvent!(new AgentStreamEvent.TextMessageContent(messageId, delta));

    private static Task EmitTextEnd(AgentTurnCallbacks callbacks, string messageId) =>
        callbacks.OnStreamEvent!(new AgentStreamEvent.TextMessageEnd(messageId));

    private static Task EmitToolStart(AgentTurnCallbacks callbacks, string toolCallId, string name, int index) =>
        callbacks.OnStreamEvent!(new AgentStreamEvent.ToolCallStart(toolCallId, name, index));

    private static Task EmitToolArgs(AgentTurnCallbacks callbacks, string toolCallId, string args) =>
        callbacks.OnStreamEvent!(new AgentStreamEvent.ToolCallArgs(toolCallId, args));

    private static Task EmitToolEnd(AgentTurnCallbacks callbacks, string toolCallId) =>
        callbacks.OnStreamEvent!(new AgentStreamEvent.ToolCallEnd(toolCallId));

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
