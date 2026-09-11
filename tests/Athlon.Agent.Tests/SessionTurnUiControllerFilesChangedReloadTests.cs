using System.Text.Json;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

/// <summary>
/// INV-3: while a turn still owns live FILES_CHANGED / TURN_ACTIVITY cards, a refresh-style
/// reload is suppressed instead of re-rendering. A full replay mid-turn is what stacked a second
/// files card next to the live one. The turn-end authoritative replay is what renders the
/// canonical surface, so a suppressed refresh needs no deferred flush.
/// </summary>
[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class SessionTurnUiControllerFilesChangedReloadTests
{
    [Fact]
    public async Task Settings_refresh_during_live_turn_is_suppressed_for_the_turn()
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

        var session = AgentSession.Create("defer-settings-refresh");
        var callbacks = ui.BuildCallbacks(new LiveAgentSession(session));

        await dispatcher.InvokeAsync(() => ui.ResetForTurn());
        await EmitFileWrite(callbacks, "call-write-1", "src/App.tsx");
        // A successful file_write result is dropped from the activity fold, so keep one
        // in-flight activity tool: it anchors the live-turn surface across the refresh.
        await EmitPendingActivityTool(callbacks, "call-read-1", "file_read", "src/Other.cs");

        // Precondition: the turn really owns a live files card.
        Assert.True(await dispatcher.InvokeAsync(() => ui.ModifiedFiles.Count) > 0);

        await ui.RefreshDisplayForSettingsAsync();

        // Suppressed: the live turn still owns the timeline.
        Assert.Equal(0, Volatile.Read(ref reloadCount));

        await dispatcher.InvokeAsync(() => ui.FinalizeTurn(
            session,
            [ChatMessage.Create(MessageRole.User, "edit it")],
            cancelled: false,
            timedOut: false,
            turnTimeoutMinutes: 30));

        // Turn end renders the canonical surface exactly once.
        Assert.Equal(1, Volatile.Read(ref reloadCount));
    }

    [Fact]
    public async Task Reload_is_not_deferred_when_no_live_turn()
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

        await ui.RefreshDisplayForSettingsAsync();

        Assert.Equal(1, Volatile.Read(ref reloadCount));
        Assert.False(ui.HasModifiedFiles);
    }

    [Fact]
    public async Task HydrateDisplay_during_live_turn_still_reloads_once()
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

        var session = AgentSession.Create("authoritative-hydrate");
        var callbacks = ui.BuildCallbacks(new LiveAgentSession(session));

        await dispatcher.InvokeAsync(() => ui.ResetForTurn());
        await EmitFileWrite(callbacks, "call-write-2", "src/App.tsx");
        Assert.True(await dispatcher.InvokeAsync(() => ui.ModifiedFiles.Count) > 0);

        // Authoritative render (session switch / first paint) must not be deferred.
        await ui.HydrateDisplayAsync(
            session,
            [ChatMessage.Create(MessageRole.User, "edit it")]);

        Assert.Equal(1, Volatile.Read(ref reloadCount));
    }

    [Fact]
    public async Task Suppressed_refresh_is_not_flushed_on_a_later_turn()
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

        var session = AgentSession.Create("no-stale-deferral");
        var callbacks = ui.BuildCallbacks(new LiveAgentSession(session));

        await dispatcher.InvokeAsync(() => ui.ResetForTurn());
        await EmitFileWrite(callbacks, "call-write-3", "src/App.tsx");
        await EmitPendingActivityTool(callbacks, "call-read-3", "file_read", "src/Other.cs");
        await ui.RefreshDisplayForSettingsAsync();
        Assert.Equal(0, Volatile.Read(ref reloadCount));

        // Turn end renders the canonical surface exactly once — the suppressed refresh above is
        // not queued behind it (which would double-render).
        await dispatcher.InvokeAsync(() => ui.FinalizeTurn(
            session,
            Array.Empty<ChatMessage>(),
            cancelled: false,
            timedOut: false,
            turnTimeoutMinutes: 30));
        Assert.Equal(1, Volatile.Read(ref reloadCount));

        // A later turn's refresh is suppressed on its own merits; nothing leaks from the first turn.
        await dispatcher.InvokeAsync(() => ui.ResetForTurn());
        await EmitPendingActivityTool(callbacks, "call-read-4", "file_read", "src/Another.cs");
        await ui.RefreshDisplayForSettingsAsync();

        Assert.Equal(1, Volatile.Read(ref reloadCount));
    }

    /// <summary>
    /// Regression: switching away mid-turn and back rebuilds the display from disk, whose activity
    /// source carries the *transcript's* user message id. The live cards were anchored to the
    /// provisional id AddUserMessage minted, so without re-anchoring the switch-back replayed fold
    /// and the live upsert registered under two different entry ids and rendered side by side.
    /// </summary>
    [Fact]
    public async Task Switch_back_mid_turn_reanchors_live_cards_to_the_transcript_user_message()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.ReloadChatViewOverride = () => Task.CompletedTask;
        ui.SetDisplayed(true);

        var session = AgentSession.Create("switch-anchor");
        var callbacks = ui.BuildCallbacks(new LiveAgentSession(session));
        await dispatcher.InvokeAsync(() => ui.ResetForTurn());

        // The UI mints its own provisional user row; the runtime would persist a different id.
        await dispatcher.InvokeAsync(() => ui.AddUserMessage("edit it", Array.Empty<ImageAttachment>()));

        await EmitFileWrite(callbacks, "call-write-anchor", "src/App.tsx");
        await EmitPendingActivityTool(callbacks, "call-read-anchor", "file_read", "src/Other.cs");
        Assert.True(await dispatcher.InvokeAsync(() => ui.ModifiedFiles.Count) > 0);

        var provisionalAnchor = ui.CurrentTurnAnchorId;
        Assert.False(string.IsNullOrWhiteSpace(provisionalAnchor));

        // The turn is still in flight while the user switches away and back.
        ui.SetDisplayed(false);

        var transcriptUser = ChatMessage.Create(MessageRole.User, "edit it");
        var transcriptRead = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-read-anchor",
                "Tool `file_read` succeeded.",
                "",
                "Arguments: path = src/Other.cs",
                "Summary: Read src/Other.cs",
                ""));
        var persistedSession = session.WithMessages(
            [transcriptUser, transcriptRead]);

        await ui.HydrateDisplayAsync(
            persistedSession,
            [transcriptUser],
            synthesizeInterruptedToolResults: false,
            activitySourceMessages: persistedSession.Messages,
            preserveActiveTurn: true);
        ui.SetDisplayed(true);

        // The replay keys the fold off the transcript user message; the live upsert must adopt
        // that same id or the two cards cannot collapse.
        Assert.Equal(transcriptUser.Id, ui.CurrentTurnAnchorId);
        Assert.NotEqual(provisionalAnchor, ui.CurrentTurnAnchorId);
    }

    /// <summary>
    /// A rebuild from disk restores each succeeded edit as its own card, keyed by tool call id, so
    /// the live re-publish after a switch-back rewrites the replayed cards in place.
    /// </summary>
    [Fact]
    public void RebuildFromMessages_restores_one_edit_card_per_succeeded_file_tool()
    {
        var tracker = new SessionModifiedFilesTracker();
        tracker.RebuildFromMessages(
        [
            new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "edit two files")),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: call-a",
                    "Tool `file_edit` succeeded.",
                    "",
                    "Arguments: path = a.ts",
                    "Summary: Edited a.ts",
                    "",
                    "--- a/a.ts",
                    "+++ b/a.ts",
                    "@@ -1,1 +1,1 @@",
                    "-old",
                    "+new"))),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: call-b",
                    "Tool `file_write` succeeded.",
                    "",
                    "Arguments: path = b.ts; content = hello",
                    "Summary: Wrote 5 chars")))
        ]);

        var cards = tracker.PeekSegmentEditCards();
        Assert.Equal(2, cards.Count);
        Assert.Equal(["call-a", "call-b"], cards.Select(card => card.ToolCallId));

        // Rebuilding drops the previous cards instead of accumulating them.
        tracker.RebuildFromMessages(
        [
            new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "edit two files")),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: call-a",
                    "Tool `file_edit` succeeded.",
                    "",
                    "Arguments: path = a.ts",
                    "Summary: Edited a.ts",
                    "",
                    "--- a/a.ts",
                    "+++ b/a.ts",
                    "@@ -1,1 +1,1 @@",
                    "-old",
                    "+new")))
        ]);

        Assert.Equal(["call-a"], tracker.PeekSegmentEditCards().Select(card => card.ToolCallId));
    }

    /// <summary>
    /// Emits a succeeded file write through the live stream and returns the per-edit card the
    /// tracker staged for it.
    /// </summary>
    [Fact]
    public void Live_file_write_result_stages_a_card_keyed_by_tool_call_id()
    {
        var tracker = new SessionModifiedFilesTracker();
        tracker.BeginTurn();
        tracker.Process(new AgentStreamEvent.ToolCallStart("call-live", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-live", """{"path":"c.ts","content":"hi"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd("call-live"));
        tracker.Process(new AgentStreamEvent.ToolCallResult(
            "call-live",
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-live",
                "Tool `file_write` succeeded.",
                "",
                "Arguments: path = c.ts; content = hi",
                "Summary: Wrote 2 chars"),
            "msg-live"));

        var card = Assert.Single(tracker.PeekSegmentEditCards());
        Assert.Equal("call-live", card.ToolCallId);
        var file = Assert.Single(card.Files);
        Assert.Equal("c.ts", file.RelativePath);

        // Sealing the segment drops the staged card so the next turn republishes its own.
        tracker.ClearSegmentEditCards();
        Assert.Empty(tracker.PeekSegmentEditCards());
    }

    private static async Task EmitFileWrite(AgentTurnCallbacks callbacks, string toolCallId, string path)
    {
        await callbacks.OnStreamEvent!(new AgentStreamEvent.ToolCallStart(toolCallId, "file_write", 0));
        await callbacks.OnStreamEvent!(
            new AgentStreamEvent.ToolCallArgs(
                toolCallId,
                $"{{\"path\":\"{path}\",\"content\":\"line\"}}"));
        await callbacks.OnStreamEvent!(new AgentStreamEvent.ToolCallEnd(toolCallId));
        await callbacks.OnStreamEvent!(
            new AgentStreamEvent.ToolCallResult(
                toolCallId,
                string.Join(
                    Environment.NewLine,
                    $"ToolCallId: {toolCallId}",
                    "Tool `file_write` succeeded.",
                    "",
                    $"Arguments: path = {path}",
                    "Summary: Wrote 1 line"),
                "tool-message-1"));
    }

    /// <summary>
    /// Emits an activity tool that stays in flight (no result). A successful file tool result is
    /// dropped from the activity fold, so this is what keeps the live-turn surface — and with it
    /// the deferred reload — in place across a display rebuild.
    /// </summary>
    private static async Task EmitPendingActivityTool(
        AgentTurnCallbacks callbacks,
        string toolCallId,
        string toolName,
        string path)
    {
        await callbacks.OnStreamEvent!(new AgentStreamEvent.ToolCallStart(toolCallId, toolName, 0));
        await callbacks.OnStreamEvent!(
            new AgentStreamEvent.ToolCallArgs(toolCallId, $"{{\"path\":\"{path}\"}}"));
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
