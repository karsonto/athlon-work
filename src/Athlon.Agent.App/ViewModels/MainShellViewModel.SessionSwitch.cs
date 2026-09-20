using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Navigation;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Infrastructure.Ssh;
using MaterialDesignThemes.Wpf;

namespace Athlon.Agent.App.ViewModels;

/// <summary>
/// Session switch orchestration: opening a session by id, loading and adopting its snapshot,
/// and applying the resulting shell chrome. Split out of the shell orchestration file.
/// </summary>
public partial class MainShellViewModel
{
    public async Task OpenSessionByIdAsync(string sessionId)
    {
        CurrentPage = AppPage.Chat;
        SessionSwitchProfiler.Begin(sessionId);
        try
        {
            if (!string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                using (SessionSwitchProfiler.Measure(SessionSwitchPhases.Prepare))
                {
                    await PrepareSessionForSwitchAsync(_session).ConfigureAwait(true);
                }
            }

            await LoadSessionInternalAsync(sessionId);
        }
        finally
        {
            SessionSwitchProfiler.Complete();
        }
    }

    private void CancelPendingSessionLoad() =>
        Interlocked.Increment(ref _sessionLoadGeneration);

    private bool IsSessionLoadCurrent(int loadGeneration) =>
        loadGeneration == Volatile.Read(ref _sessionLoadGeneration);

    private async Task LoadSessionInternalAsync(string sessionId)
    {
        var loadGeneration = Interlocked.Increment(ref _sessionLoadGeneration);
        IsLoadingSession = true;
        SetComposerStatus(_loc["Shell_LoadingConversation"]);
        try
        {
            SessionNavigationSnapshot? snapshot;
            using (SessionSwitchProfiler.Measure(SessionSwitchPhases.SnapshotLoad))
            {
                snapshot = await _sessionNavigation.LoadSnapshotAsync(sessionId).ConfigureAwait(true);
            }

            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            if (snapshot is null)
            {
                SetComposerStatus(null);
                ShowShellToast(_loc["Shell_LoadConversationFailed"], ShellToastKind.Error);
                return;
            }

            var preserveActiveTurn = _sessionTurns.TurnHost.IsRunning(sessionId);
            // A session with a running turn already holds live in-memory state. Keep it instead of
            // downgrading the entry to the metadata shell (which would also clear SessionComplete
            // and suppress the turn's session.json writes while the background load is in flight).
            var attachedSession = snapshot.Session;
            if (preserveActiveTurn
                && _runtime.TryGetEntry(sessionId, out var runningEntry)
                && runningEntry.Session is not null)
            {
                attachedSession = runningEntry.Session;
            }
            else
            {
                _runtime.Attach(snapshot.Session, sessionComplete: !snapshot.SessionIsPartial);
            }

            SwitchDisplayedSession(attachedSession);
            _olderDisplayCursor = snapshot.OlderDisplayCursor;
            _activeUi.UpdateSurfaceCursor(_olderDisplayCursor);
            ApplyLoadedSessionChrome();

            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            var displayMessages = snapshot.DisplayMessages;
            var firstPaintDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _displayedSessionReady = LoadFullSessionAndAdoptAsync(
                sessionId,
                loadGeneration,
                displayMessages,
                preserveActiveTurn,
                firstPaintDone.Task);
            try
            {
                // First paint: the display page is enough to render the timeline. When the session
                // is still a metadata-only shell this shows history immediately instead of blocking
                // on the full session.json deserialization.
                await _activeUi.HydrateDisplayAsync(
                    _session,
                    displayMessages,
                    synthesizeInterruptedToolResults: false,
                    activitySourceMessages: snapshot.ActivitySource,
                    preserveActiveTurn: preserveActiveTurn).ConfigureAwait(true);

                _runtime.MarkHydrated(sessionId, _olderDisplayCursor);

                if (_savedChatView is not null)
                {
                    await _savedChatView.SetOlderMessagesAvailableAsync(
                        _olderDisplayCursor is not null).ConfigureAwait(true);
                }

                SetComposerStatus(null);
                ShowShellToast(_loc.Format("Shell_LoadConversationDone", snapshot.Session.Title), ShellToastKind.Success);

                // First paint is on screen: hide the loading overlay now instead of waiting for the
                // full payload. This is the point of the two-phase load — the timeline is visible
                // while session.json finishes loading in the background.
                SessionSwitchProfiler.RecordElapsed(SessionSwitchPhases.FirstPaint);
                IsLoadingSession = false;
                NotifyCommandStatesChanged();

                firstPaintDone.TrySetResult();
                await _displayedSessionReady.ConfigureAwait(true);
            }
            finally
            {
                // Never leave the adopt task (and any send awaiting it) hanging.
                firstPaintDone.TrySetResult();
            }

            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            ApplySessionWorkspace();
            UpdateDisplayedBusyState();
        }
        finally
        {
            if (IsSessionLoadCurrent(loadGeneration))
            {
                IsLoadingSession = false;
                if (!StatusFeedback.IsComposerStatusVisible
                    || StatusFeedback.ComposerStatusText == _loc["Shell_LoadingConversation"])
                {
                    SetComposerStatus(null);
                }

                NotifyCommandStatesChanged();
            }
        }
    }

    /// <summary>
    /// Completes a two-phase session load: awaits the full session payload and adopts it once the
    /// user can actually interact with the conversation. Awaiters (<see cref="EnsureDisplayedSessionReadyAsync"/>)
    /// are gated on this task, so a turn can never start from the metadata-only shell.
    /// </summary>
    private async Task LoadFullSessionAndAdoptAsync(
        string sessionId,
        int loadGeneration,
        IReadOnlyList<ChatMessage> displayMessages,
        bool preserveActiveTurn,
        Task firstPaintDone)
    {
        AgentSession? full;
        try
        {
            full = await _sessionNavigation.LoadFullSessionAsync(sessionId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Full session load failed for {sessionId}: {ex.Message}");
            if (IsSessionLoadCurrent(loadGeneration))
            {
                ShowShellToast(_loc["Shell_LoadConversationFailed"], ShellToastKind.Error);
            }

            return;
        }

        // The full payload is ready, but do not adopt it until the display page has been painted:
        // adopting earlier would make the ready-task complete while the timeline is still empty.
        await firstPaintDone.ConfigureAwait(true);
        if (full is null)
        {
            if (IsSessionLoadCurrent(loadGeneration))
            {
                ShowShellToast(_loc["Shell_LoadConversationFailed"], ShellToastKind.Error);
            }

            return;
        }

        if (!IsSessionLoadCurrent(loadGeneration))
        {
            return;
        }

        // While the session loaded, a running turn may have produced newer in-memory messages.
        // Prefer that live session over the (possibly older) on-disk snapshot so the delayed load
        // never rolls the conversation back.
        var adopted = full;
        if (preserveActiveTurn
            && _runtime.TryGetEntry(sessionId, out var live)
            && live.Session is not null
            && live.Session.UpdatedAt > full.UpdatedAt)
        {
            adopted = live.Session;
        }

        // We hold the full payload now: flip the entry back to SessionComplete so the flush path
        // may persist again (the guard in ReplaceDisplayAsync/FlushSessionCore depends on it), and
        // refresh the navigation cache so switching back skips the reload.
        _runtime.Attach(adopted, sessionComplete: true);
        _session = adopted;
        _sessionNavigation.UpdateCachedSession(adopted);

        // A session that had no display rows yet (fresh or migrated) can only be rebuilt now, once
        // the messages are actually in memory.
        if (displayMessages.Count == 0 && adopted.Messages.Count > 0)
        {
            await _runtime.ReplaceDisplayAsync(adopted, adopted.Messages).ConfigureAwait(true);
            _sessionNavigation.InvalidateDisplayPage(sessionId);
            var rebuiltMessages = adopted.Messages
                .TakeLast(ConversationDisplayLimits.PageSize)
                .ToArray();

            if (!IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            await _activeUi.HydrateDisplayAsync(
                adopted,
                rebuiltMessages,
                synthesizeInterruptedToolResults: false,
                activitySourceMessages: rebuiltMessages,
                preserveActiveTurn: preserveActiveTurn).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Resolves once the displayed session's full payload has been adopted (or immediately when
    /// there is nothing to wait for). Turn-start paths await this so a metadata-only shell can
    /// never be used as the model context. Never throws: a failed background load already raised a
    /// toast from the load path, and letting it escape an async-void command would crash the shell.
    /// </summary>
    internal async Task EnsureDisplayedSessionReadyAsync()
    {
        var ready = _displayedSessionReady;
        if (ready is null || ready.IsCompleted)
        {
            return;
        }

        try
        {
            await ready.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Waiting for the displayed session to finish loading failed: {ex.Message}");
        }
    }

    private void ApplyLoadedSessionChrome()
    {
        CurrentSessionTitle = _session.Title;
        KnowledgePageVm.SetSession(_displayedSessionId);
        // Restore this conversation's draft instead of wiping it, so an A -> B -> A switch keeps
        // whatever the user had typed. With preservation off, fall back to the old clear-on-switch.
        if (_appSettings.Ui.PreserveSessionUiState)
        {
            ChatPage.RestoreComposerDraft(_displayedSessionId);
        }
        else
        {
            ChatPage.ComposerText = string.Empty;
        }
        PendingImageAttachments.Clear();
        PendingDocumentAttachments.Clear();
    }

    private void ConfigureWorkspaceWatcher() =>
        _workspaceBridge.ConfigureWatcher(
            _session,
            _workspaceContext,
            path => FileEditor.HandleExternalFileChange(path),
            () =>
            {
                RefreshAtCompletionSources();
                _ = RefreshWorkspaceTreeAsync();
            });

    private void SwitchDisplayedSession(AgentSession session)
    {
        // Any pending full-session load belonged to the previous session; the load path sets a new
        // ready-task for the session it adopts. Non-load switches (new chat, clear) are ready
        // immediately because there is no payload to wait for.
        _displayedSessionReady = null;
        UnwireSessionUsageUi(_activeUi);
        _activeUi.SetDisplayed(false);
        _activeUi.Messages.CollectionChanged -= OnMessagesCollectionChanged;

        // The previous controller is intentionally kept in the LRU cache so switching back reuses
        // its rendered view models. Eviction is capacity-based (see SessionUiCache) and pinned
        // sessions stay put; explicit removal only happens on session delete.
        _displayedSessionId = session.Id;
        // Non-turn (UI) SSH callers resolve the slot of the session that is currently visible.
        _sshConnection.SetDefaultSession(_displayedSessionId);
        _session = session;
        // Note: _runtime.Attach is intentionally NOT called here. Every caller (startup, new
        // session, switch, load) attaches first with the right SessionComplete flag; attaching
        // again with the default would clobber a metadata-only entry back to "complete".
        _activeUi = _uiCache.GetOrCreate(_displayedSessionId, RequestScrollToBottom, RequestScrollToBottomImmediate);
        WireSessionUsageUi(_activeUi);
        _activeUi.SetDisplayed(true);
        if (_activeUi.ChatView is null)
        {
            _activeUi.ChatView = _savedChatView;
        }

        _activeUi.Messages.CollectionChanged += OnMessagesCollectionChanged;
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(HasComputerUseTranscript));
        OnPropertyChanged(nameof(QueuedTurns));
        OnPropertyChanged(nameof(HasQueuedTurns));
        UpdateDisplayedBusyState();
        if (IsComputerUseOverlayActive)
        {
            AttachComputerUseStatusMessageListeners();
            RefreshComputerUseStatus();
        }

        KnowledgePageVm.SetSession(_displayedSessionId);
        _ = ComposerKnowledge.LoadForSessionAsync(_displayedSessionId);
        _ = ComposerHarness.LoadForSessionAsync(_displayedSessionId);
        DebugBar.RefreshFromActiveRun();
        // The plan card belongs to the session being displayed: refresh re-publishes the new
        // session's active plan, or clears the card for one that has no active plan.
        PlanBar.RefreshFromActiveRun();
        RefreshPlanCard();
        QuestionBar.RefreshFromActiveSession();
        RequestRefreshSessionHistory();
    }

    /// <summary>
    /// Flushes durable state before leaving a session. Ephemeral startup shells (empty and never
    /// saved) are dropped so history navigation does not add a spurious "New Chat" index entry.
    /// </summary>
    /// <returns>True when the session remains in the runtime store after preparation.</returns>
    private async Task<bool> PrepareSessionForSwitchAsync(AgentSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Id))
        {
            return false;
        }

        // Keep the outgoing conversation's draft text so switching back restores it.
        if (_appSettings.Ui.PreserveSessionUiState)
        {
            ChatPage.SaveComposerDraft(session.Id);
        }

        if (!await _sessionNavigation.ShouldPersistOnSwitchAsync(session).ConfigureAwait(true))
        {
            DiscardEphemeralSession(session.Id);
            return false;
        }

        await FlushSessionForSwitchAsync(session.Id).ConfigureAwait(true);
        return true;
    }

    private void DiscardEphemeralSession(string sessionId)
    {
        if (_uiCache.TryGet(sessionId, out var ui)
            && ui is not null
            && string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            ui.SetDisplayed(false);
        }

        _runtime.Remove(sessionId);
        _sessionNavigation.Invalidate(sessionId);
    }

    /// <summary>
    /// Checkpoints mid-turn streaming text, flushes pending transcript rows, and invalidates
    /// navigation cache so the next load reads durable disk state.
    /// </summary>
    private async Task FlushSessionForSwitchAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (_uiCache.TryGet(sessionId, out var ui) && ui is not null)
        {
            if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                ui.SetDisplayed(false);
            }

            if (_sessionTurns.TurnHost.IsRunning(sessionId))
            {
                foreach (var message in ui.CaptureStreamingCheckpoint())
                {
                    await _runtime.UpsertAsync(sessionId, message).ConfigureAwait(true);
                }
            }
        }

        try
        {
            await _runtime.FlushSessionAsync(sessionId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Flush before session switch failed for {sessionId}: {ex.Message}");
        }

        // Keep the session payload so switching back does not deserialize session.json again, but
        // only when it is actually complete: a metadata-only shell must never be cached as a full
        // session (it would make the next switch skip the real load).
        if (_runtime.TryGetHydrated(sessionId, out var live)
            && live.SessionComplete
            && live.Session is not null)
        {
            _sessionNavigation.UpdateCachedSession(live.Session);
        }
        else
        {
            _sessionNavigation.Invalidate(sessionId);
        }

        // The conversation log may have grown during the turn, so the tail page is always stale.
        _sessionNavigation.InvalidateDisplayPage(sessionId);
    }
}
