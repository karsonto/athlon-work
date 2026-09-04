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
    public async Task SyncActivitySourceFromSession_includes_folded_file_tools_for_replay()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () => Task.CompletedTask;
        ui.SetDisplayed(true);

        var user = ChatMessage.Create(MessageRole.User, "edit it");
        var edit = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-1",
                "Tool `file_edit` succeeded.",
                "",
                "Arguments: path = server.ts",
                "Summary: Edited",
                "",
                "--- a/server.ts",
                "+++ b/server.ts",
                "@@ -1,1 +1,1 @@",
                "-a",
                "+b"));
        var assistant = ChatMessage.Create(MessageRole.Assistant, "done");
        var session = AgentSession.Create("switch-files").WithMessages([user, edit, assistant]);

        await dispatcher.InvokeAsync(() =>
        {
            ui.Messages.Add(new ChatMessageViewModel(user));
            ui.Messages.Add(new ChatMessageViewModel(assistant));
            ui.SyncActivitySourceFromSession(session);
        });

        var source = await dispatcher.InvokeAsync(() => ui.ActivitySourceMessages.ToList());
        Assert.Contains(source, message => message.Role == MessageRole.Tool);

        var display = await dispatcher.InvokeAsync(() => ui.Messages.ToList());
        var events = ChatEventSerializer.BuildReplayEvents(
            display,
            showToolCalls: false,
            activitySourceMessages: source);
        Assert.Contains(events, json => json.Contains("FILES_CHANGED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncActivitySourceFromSession_backfills_turn_start_when_display_starts_mid_turn()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () => Task.CompletedTask;
        ui.SetDisplayed(true);

        var user = ChatMessage.Create(MessageRole.User, "分析项目代码");
        var earlyRead = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-early",
                "Tool `file_read` succeeded.",
                "",
                "Arguments: path = src/Early.cs",
                "Summary: Read",
                "",
                "1|ok"));
        var lateReads = Enumerable.Range(1, 5)
            .Select(i => ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    $"ToolCallId: call-{i}",
                    "Tool `file_read` succeeded.",
                    "",
                    $"Arguments: path = src/Late{i}.cs",
                    "Summary: Read",
                    "",
                    "1|ok")))
            .ToArray();
        var assistant = ChatMessage.Create(MessageRole.Assistant, "done");
        var session = AgentSession.Create("backfill")
            .WithMessages([user, earlyRead, .. lateReads, assistant]);

        await dispatcher.InvokeAsync(() =>
        {
            // Simulate a truncated display page that starts mid-turn.
            foreach (var read in lateReads)
            {
                ui.Messages.Add(new ChatMessageViewModel(read));
            }

            ui.Messages.Add(new ChatMessageViewModel(assistant));
            ui.SyncActivitySourceFromSession(session);
        });

        var source = await dispatcher.InvokeAsync(() => ui.ActivitySourceMessages.ToList());
        Assert.Equal(MessageRole.User, source[0].Role);
        Assert.Contains(source, message => message.Id == earlyRead.Id);

        var display = await dispatcher.InvokeAsync(() => ui.Messages.ToList());
        var events = ChatEventSerializer.BuildReplayEvents(
            display,
            showToolCalls: false,
            activitySourceMessages: source);
        var activityJson = Assert.Single(events, json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal));
        using var doc = System.Text.Json.JsonDocument.Parse(activityJson);
        Assert.Equal(6, doc.RootElement.GetProperty("exploredFileCount").GetInt32());
    }

    [Fact]
    public async Task ReplayActivitySource_slices_to_displayed_user_window()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () => Task.CompletedTask;
        ui.SetDisplayed(true);

        static ChatMessage Read(string id, string path) => ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                $"ToolCallId: {id}",
                "Tool `file_read` succeeded.",
                "",
                $"Arguments: path = {path}",
                $"Summary: Read {path}",
                ""));

        var oldUser = ChatMessage.Create(MessageRole.User, "old");
        var oldRead = Read("old-read", "Old.cs");
        var oldAssistant = ChatMessage.Create(MessageRole.Assistant, "old done");
        var newUser = ChatMessage.Create(MessageRole.User, "new");
        var newRead = Read("new-read", "New.cs");
        var newAssistant = ChatMessage.Create(MessageRole.Assistant, "new done");
        var session = AgentSession.Create("slice").WithMessages(
            [oldUser, oldRead, oldAssistant, newUser, newRead, newAssistant]);

        await ui.HydrateDisplayAsync(
            session,
            [newUser, newAssistant],
            synthesizeInterruptedToolResults: false,
            activitySourceMessages: [oldUser, oldRead, oldAssistant, newUser, newRead, newAssistant]);

        var replay = await dispatcher.InvokeAsync(() => ui.ReplayActivitySource.ToList());
        Assert.Equal(newUser.Id, replay[0].Id);
        Assert.DoesNotContain(replay, message => message.Id == oldUser.Id);
        Assert.Contains(replay, message => message.Id == newRead.Id);
    }

    [Fact]
    public async Task PrependDisplayMessagesAsync_updates_surface_snapshot_and_visible_messages()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () => Task.CompletedTask;
        ui.SetDisplayed(false);

        var oldUser = ChatMessage.Create(MessageRole.User, "old");
        var newUser = ChatMessage.Create(MessageRole.User, "new");
        var newAssistant = ChatMessage.Create(MessageRole.Assistant, "done");

        await ui.HydrateDisplayAsync(
            AgentSession.Create("prepend").WithMessages([oldUser, newUser, newAssistant]),
            [newUser, newAssistant],
            synthesizeInterruptedToolResults: false,
            activitySourceMessages: [newUser, newAssistant]);

        await ui.PrependDisplayMessagesAsync(
            [oldUser],
            new ConversationDisplayCursor(0, Array.Empty<string>()),
            showToolCalls: false,
            hasOlderMessages: false);

        var visible = await dispatcher.InvokeAsync(() => ui.Messages.ToList());
        var displaySnapshot = await dispatcher.InvokeAsync(() => ui.DisplayMessagesSnapshot.ToList());

        Assert.Equal(oldUser.Id, visible[0].MessageId);
        Assert.Equal(oldUser.Id, displaySnapshot[0].Id);
        Assert.Equal(3, displaySnapshot.Count);
    }

    [Fact]
    public async Task PrependDisplayMessagesAsync_keeps_messages_beyond_legacy_trim_threshold()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () => Task.CompletedTask;
        ui.SetDisplayed(false);

        var tail = Enumerable.Range(200, 50)
            .Select(i => ChatMessage.Create(MessageRole.User, $"msg-{i}"))
            .ToArray();
        var older = Enumerable.Range(0, 200)
            .Select(i => ChatMessage.Create(MessageRole.User, $"msg-{i}"))
            .ToArray();

        await ui.HydrateDisplayAsync(
            AgentSession.Create("long").WithMessages(older.Concat(tail).ToArray()),
            tail,
            synthesizeInterruptedToolResults: false,
            activitySourceMessages: older.Concat(tail).ToArray());

        await ui.PrependDisplayMessagesAsync(
            older,
            olderDisplayCursor: null,
            showToolCalls: false,
            hasOlderMessages: false);

        var visible = await dispatcher.InvokeAsync(() => ui.Messages.ToList());
        Assert.Equal(250, visible.Count);
        Assert.DoesNotContain(visible, message => message.IsHiddenPlaceholder);
        Assert.Equal(older[0].Id, visible[0].MessageId);
        Assert.Equal(tail[^1].Id, visible[^1].MessageId);
    }

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
    public async Task SwitchingAway_flushes_already_received_text_into_session_cache()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(true);
        var callbacks = ui.BuildCallbacks();
        await EmitText(callbacks, MessageId1, "cached before switch");

        Assert.Empty(await dispatcher.InvokeAsync(() => ui.Messages.ToList()));

        ui.SetDisplayed(false);

        var assistant = await dispatcher.InvokeAsync(() =>
            ui.Messages.LastOrDefault(message => !message.IsUser && !message.IsTool));
        Assert.NotNull(assistant);
        Assert.Contains("cached before switch", assistant!.Content, StringComparison.Ordinal);
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
    public async Task HydrateDisplayAsync_empty_list_does_not_pull_session_messages_into_ui()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () => Task.CompletedTask;
        ui.SetDisplayed(true);

        var huge = Enumerable.Range(0, 200)
            .Select(i => ChatMessage.Create(MessageRole.User, $"msg-{i}"))
            .ToArray();
        var session = AgentSession.Create("huge").WithMessages(huge);

        await ui.HydrateDisplayAsync(session, Array.Empty<ChatMessage>(), synthesizeInterruptedToolResults: false);

        var count = await dispatcher.InvokeAsync(() => ui.Messages.Count);
        Assert.Equal(0, count);
        Assert.Equal(200, session.Messages.Count);
    }

    [Fact]
    public async Task HiddenSession_rebuilds_cached_surface_after_compaction_replaces_history()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        var oldUser = ChatMessage.Create(MessageRole.User, "old");
        var oldAssistant = ChatMessage.Create(MessageRole.Assistant, "old answer");
        var original = AgentSession.Create("compact-hidden")
            .WithMessages([oldUser, oldAssistant]);
        await ui.HydrateDisplayAsync(
            original,
            original.Messages,
            synthesizeInterruptedToolResults: false,
            activitySourceMessages: original.Messages);
        ui.SetDisplayed(false);

        var compaction = ChatMessage.Create(MessageRole.Compaction, "compacted");
        var currentAssistant = ChatMessage.Create(MessageRole.Assistant, "current");
        var compacted = AgentSession.Create("compact-hidden")
            .WithMessages([compaction, currentAssistant]);
        await ui.BuildCallbacks().OnSessionUpdated!(compacted);

        var display = await dispatcher.InvokeAsync(() => ui.DisplayMessagesSnapshot.ToList());
        Assert.DoesNotContain(display, message => message.Id == oldUser.Id);
        Assert.Contains(display, message => message.Id == compaction.Id);
        Assert.Contains(display, message => message.Id == currentAssistant.Id);
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
    public async Task ReloadChatViewAsync_skips_shared_view_when_session_is_hidden()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var reloadCount = 0;
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () =>
        {
            Interlocked.Increment(ref reloadCount);
            return Task.CompletedTask;
        };
        ui.SetDisplayed(true);
        ui.SetDisplayed(false);

        await ui.ReloadChatViewAsync();

        Assert.Equal(0, Volatile.Read(ref reloadCount));
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
        // computer_* stay as tool cards; activity tools (file_read etc.) never enter Messages.
        await EmitToolStart(callbacks, "call-1", "computer_observe", 0);
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

    [Fact]
    public async Task CaptureStreamingCheckpoint_includes_buffered_assistant_text()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(true);
        var callbacks = ui.BuildCallbacks();
        await EmitText(callbacks, MessageId1, "checkpoint text");

        var checkpoint = ui.CaptureStreamingCheckpoint();
        var assistant = Assert.Single(checkpoint);
        Assert.Equal(MessageId1, assistant.Id);
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        Assert.Contains("checkpoint text", assistant.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HydrateDisplayAsync_preserveActiveTurn_keeps_live_assistant_over_disk()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () => Task.CompletedTask;
        ui.SetDisplayed(true);
        var callbacks = ui.BuildCallbacks();
        await EmitText(callbacks, MessageId1, "live newer");

        var user = ChatMessage.Create(MessageRole.User, "q");
        var diskAssistant = ChatMessage.CreateWithId(MessageId1, MessageRole.Assistant, "disk older");
        await ui.HydrateDisplayAsync(
            AgentSession.Create("preserve").WithMessages([user, diskAssistant]),
            [user, diskAssistant],
            synthesizeInterruptedToolResults: false,
            activitySourceMessages: [user, diskAssistant],
            preserveActiveTurn: true);

        var assistant = await dispatcher.InvokeAsync(() =>
            ui.Messages.LastOrDefault(message => !message.IsUser && !message.IsTool));
        Assert.NotNull(assistant);
        Assert.Contains("live newer", assistant!.Content, StringComparison.Ordinal);
        Assert.True(assistant.IsStreaming);
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
