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
