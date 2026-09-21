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
/// Session lifecycle: create, clear/compact context, open, delete and load sessions, plus the
/// per-session usage/scroll wiring that hangs off them. Split out of the shell orchestration
/// file.
/// </summary>
public partial class MainShellViewModel
{
    [RelayCommand]
    private Task NewSession() => CreateNewSessionAsync(workspacePath: null, workspaceId: null);

    [RelayCommand(CanExecute = nameof(CanNewSessionInWorkspace))]
    private Task NewSessionInWorkspace(AgentRecordGroupViewModel? group)
    {
        if (!CanNewSessionInWorkspace(group) || group is null)
        {
            return Task.CompletedTask;
        }

        return CreateNewSessionAsync(group.WorkspacePath, group.ActiveWorkspaceId);
    }

    private static bool CanNewSessionInWorkspace(AgentRecordGroupViewModel? group) =>
        group is { HasWorkspace: true };

    private async Task CreateNewSessionAsync(string? workspacePath, string? workspaceId)
    {
        CancelPendingSessionLoad();
        IsLoadingSession = false;
        var previousSession = _session;
        var preservePrevious = await PrepareSessionForSwitchAsync(previousSession).ConfigureAwait(true);
        _session = AgentSession.Create("New Chat");
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            _session = _session.WithWorkspace(workspacePath, workspaceId);
        }

        _olderDisplayCursor = null;
        _runtime.Attach(_session, hydrated: true);
        SwitchDisplayedSession(_session);
        // Shared WebChatView keeps the previous session DOM unless we hydrate empty.
        // Without this, the first send only appends and old history stays visible.
        _activeUi.UpdateSurfaceCursor(null);
        await _activeUi.HydrateDisplayAsync(
            _session,
            Array.Empty<ChatMessage>(),
            synthesizeInterruptedToolResults: false).ConfigureAwait(true);
        if (_savedChatView is not null)
        {
            await _savedChatView.SetOlderMessagesAvailableAsync(false).ConfigureAwait(true);
        }

        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        PendingImageAttachments.Clear();
        PendingDocumentAttachments.Clear();
        UpdateDisplayedBusyState();
        CurrentPage = AppPage.Chat;
        KnowledgePageVm.SetSession(_displayedSessionId);
        _ = ComposerKnowledge.LoadForSessionAsync(_displayedSessionId);
        _ = ComposerHarness.LoadForSessionAsync(_displayedSessionId);
        ApplySessionWorkspace();
        if (preservePrevious)
        {
            _runtime.UpdateSession(previousSession);
        }

        await _storage.SaveSessionAsync(_session);
        _sessionNavigation.Invalidate(_session.Id);
        await RefreshSessionHistoryAsync();
        NotifyCommandStatesChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearContext))]
    private Task ClearContextAsync() => ClearDisplayedContextAsync();

    private async Task ClearDisplayedContextAsync()
    {
        if (!_notifier.ConfirmYesNo("Shell_ClearContextTitle", "Shell_ClearContextMessage"))
        {
            return;
        }

        if (_sessionTurns.TurnHost.IsRunning(_displayedSessionId))
        {
            _sessionTurns.TurnHost.Cancel(_displayedSessionId);
        }

        _session = _session.WithMessages(Array.Empty<ChatMessage>());
        _olderDisplayCursor = null;
        _runtime.DiscardPending(_displayedSessionId);
        await _storage.ClearConversationDisplayAsync(_session.Id);
        PendingImageAttachments.Clear();
        PendingDocumentAttachments.Clear();
        // Drop the task plan and the approved plan artifacts together: the plan is now durable on
        // disk, so clearing only one of the two would let the plan be re-injected on the next switch.
        await _planArtifactsClearer.ClearAsync(_session.Id).ConfigureAwait(true);

        await _storage.SaveSessionAsync(_session);
        _runtime.Attach(_session, hydrated: true);
        _sessionNavigation.Invalidate(_session.Id);

        await _activeUi.HydrateDisplayAsync(
            _session,
            Array.Empty<ChatMessage>(),
            synthesizeInterruptedToolResults: false).ConfigureAwait(true);
        NotifyCommandStatesChanged();
        await RefreshSessionHistoryAsync().ConfigureAwait(true);
        // The stored prompt measurement describes the payload that was just discarded. Clearing it
        // keeps the emptied meter from being inflated back to the old size, then recompute so the
        // ring reflects the empty conversation immediately.
        _compactionService.ClearPromptPressure(_session.Id);
        RefreshContextOccupancy();
    }

    [RelayCommand(CanExecute = nameof(CanCompactContext))]
    private Task CompactContextAsync() => CompactDisplayedContextAsync();

    private async Task CompactDisplayedContextAsync()
    {
        if (!_notifier.ConfirmYesNo("Shell_CompactContextTitle", "Shell_CompactContextMessage"))
        {
            return;
        }

        _compactionCts?.Cancel();
        _compactionCts?.Dispose();
        _compactionCts = new CancellationTokenSource();
        var compactionToken = _compactionCts.Token;
        IsCompacting = true;
        NotifyComposerCompactionStateChanged();
        _activeUi.BeginManualCompactionBubble();
        NotifyCommandStatesChanged();

        try
        {
            ManualCompactionResult result;
            try
            {
                await _runtime.FlushSessionAsync(_session.Id, compactionToken).ConfigureAwait(true);
                result = await _compactionService.CompactAsync(_session, compactionToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _activeUi.CancelManualCompactionBubble();
                ShowShellToast(_loc["Shell_CompactContextCancelled"], ShellToastKind.Info);
                return;
            }
            catch (Exception ex)
            {
                _activeUi.DismissManualCompactionBubble();
                ShowShellToast(ex.Message, ShellToastKind.Error);
                return;
            }

            if (!result.Compacted)
            {
                _activeUi.DismissManualCompactionBubble();
                ShowShellToast(_loc["Shell_CompactContextFailed"], ShellToastKind.Error);
                return;
            }

            _session = result.Session;
            _runtime.DiscardPending(_session.Id);
            await _runtime.ReplaceDisplayAsync(_session, _session.Messages).ConfigureAwait(true);
            _runtime.Attach(_session, hydrated: true, _olderDisplayCursor);
            _sessionNavigation.Invalidate(_session.Id);
            var audit = _session.Messages.LastOrDefault(message => message.Role == MessageRole.Compaction);
            if (audit is null)
            {
                _activeUi.DismissManualCompactionBubble();
            }
            else
            {
                _activeUi.CompleteManualCompactionBubble(audit, _session.Messages);
            }

            SessionUsageLine = SessionUsageFormatter.Format(_sessionUsageAccumulator.Get(_displayedSessionId));
            RefreshContextOccupancy();
            ShowShellToast(_loc["Shell_CompactContextDone"], ShellToastKind.Success);
            await RefreshSessionHistoryAsync().ConfigureAwait(true);
        }
        finally
        {
            IsCompacting = false;
            _compactionCts?.Dispose();
            _compactionCts = null;
            NotifyComposerCompactionStateChanged();
            NotifyCommandStatesChanged();
        }
    }

    private bool TryCancelCompaction()
    {
        if (!IsCompacting)
        {
            return false;
        }

        CancelCompaction();
        return true;
    }

    private void CancelCompaction()
    {
        if (!IsCompacting)
        {
            return;
        }

        _compactionCts?.Cancel();
    }

    private void NotifyComposerCompactionStateChanged()
    {
        ContextOccupancy.IsCompacting = IsCompacting;
        OnPropertyChanged(nameof(IsComposerStopVisible));
        OnPropertyChanged(nameof(IsComposerSendVisible));
        CompactContextCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    private bool CanCompactContext() => Messages.Count > 0 && !IsBusy && !IsCompacting;

    private bool CanClearContext() => Messages.Count > 0 && !IsBusy;

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(HasComputerUseTranscript));
        ClearContextCommand.NotifyCanExecuteChanged();
        CompactContextCommand.NotifyCanExecuteChanged();

        if (IsBusy && e.Action == NotifyCollectionChangedAction.Add)
        {
            _chatScroll.ScrollToBottom();
        }

        if (IsComputerUseOverlayActive)
        {
            SyncComputerUseStatusMessageSubscriptions(e);
            RefreshComputerUseStatus();
        }
    }

    private void NotifyCommandStatesChanged()
    {
        SendCommand.NotifyCanExecuteChanged();
        ClearContextCommand.NotifyCanExecuteChanged();
        CompactContextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasChatMessages));
    }

    private void OnQueuedTurnsChanged(string sessionId)
    {
        if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(QueuedTurns));
            OnPropertyChanged(nameof(HasQueuedTurns));
        }
    }

    [RelayCommand]
    private async Task LoadSessionAsync(SessionHistoryItemViewModel? item)
    {
        if (item is null || string.Equals(item.Id, _displayedSessionId, StringComparison.Ordinal))
        {
            return;
        }

        SessionSwitchProfiler.Begin(item.Id);
        SessionDirectoryLayout.ResetProbeStats();
        var switchStarted = Stopwatch.GetTimestamp();
        try
        {
            var previousSession = _session;
            bool preservePrevious;
            using (SessionSwitchProfiler.Measure(SessionSwitchPhases.Prepare))
            {
                preservePrevious = await PrepareSessionForSwitchAsync(previousSession).ConfigureAwait(true);
            }

            await LoadSessionInternalAsync(item.Id);
            if (preservePrevious)
            {
                _runtime.UpdateSession(previousSession);
            }

            CurrentPage = AppPage.Chat;
        }
        finally
        {
            RecordDirectoryProbeStats();
            SessionSwitchProfiler.Complete(Stopwatch.GetElapsedTime(switchStarted).TotalMilliseconds);
        }
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionHistoryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_notifier.ConfirmYesNo("Shell_DeleteConversationTitle", "Shell_DeleteConversationMessage", item.Title))
        {
            return;
        }

        if (_sessionTurns.TurnHost.IsRunning(item.Id))
        {
            _sessionTurns.TurnHost.Cancel(item.Id);
        }

        _sessionTurns.TurnHost.ClearQueue(item.Id);
        _sessionTurns.QueuedTurnPresenter.RemoveSession(item.Id);

        _runtime.Remove(item.Id);
        await _sshConnection.DisconnectSessionAsync(item.Id).ConfigureAwait(true);

        string? workspaceKey = null;
        try
        {
            var existing = await _storage.LoadSessionAsync(item.Id).ConfigureAwait(true);
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(existing.ActiveWorkspaceId))
                {
                    workspaceKey = existing.ActiveWorkspaceId;
                }
                else if (!string.IsNullOrWhiteSpace(existing.ActiveWorkspace))
                {
                    workspaceKey = MemoryScopeResolver.HashPath(existing.ActiveWorkspace);
                }
            }
        }
        catch
        {
            // Fall back to active workspace when resolving the deleted session fails.
        }

        try
        {
            await _longTermMemory.DeleteSessionMemoryAsync(workspaceKey, item.Id).ConfigureAwait(true);
        }
        catch
        {
            // Session deletion must succeed even if memory cleanup fails.
        }

        // Clear tasks/plan artifacts before deleting the directory: the clearer writes an empty
        // tasks.json, which would recreate the directory we just removed.
        // Its task-list notification triggers an async refresh (fire-and-forget on the dispatcher)
        // that would reopen tasks.json and rebuild the directory, racing the delete below. That is
        // the sharing violation behind "tasks.json is being used by another process", so mute the
        // notification for this window; the post-delete session switch reloads the composer anyway.
        _suppressTaskListRefresh = true;
        try
        {
            await _planArtifactsClearer.ClearAsync(item.Id).ConfigureAwait(true);
            await _storage.DeleteSessionAsync(item.Id);
        }
        catch (Exception ex)
        {
            // Report rather than let this escape the AsyncRelayCommand and crash the dispatcher.
            _notifier.Warning("Shell_DeleteFailedTitle", "Shell_DeleteFailedMessage", item.Title, ex.Message);
            ShowShellToast(_loc.Format("Shell_DeleteFailedStatus", ex.Message), ShellToastKind.Error);
            return;
        }
        finally
        {
            _suppressTaskListRefresh = false;
        }
        _sessionNavigation.Invalidate(item.Id);
        ChatPage.ForgetComposerDraft(item.Id);

        if (string.Equals(_session.Id, item.Id, StringComparison.Ordinal))
        {
            _session = AgentSession.Create("New Chat");
            _olderDisplayCursor = null;
            _runtime.Attach(_session, hydrated: true);
            SwitchDisplayedSession(_session);
            _activeUi.UpdateSurfaceCursor(null);
            await _activeUi.HydrateDisplayAsync(
                _session,
                Array.Empty<ChatMessage>(),
                synthesizeInterruptedToolResults: false).ConfigureAwait(true);
            if (_savedChatView is not null)
            {
                await _savedChatView.SetOlderMessagesAvailableAsync(false).ConfigureAwait(true);
            }

            CurrentSessionTitle = _session.Title;
            ComposerText = string.Empty;
            PendingImageAttachments.Clear();
            PendingDocumentAttachments.Clear();
            ApplySessionWorkspace();
            CurrentPage = AppPage.Chat;
        }

        await RefreshSessionHistoryAsync();
        ShowShellToast(_loc["Shell_DeleteConversationDone"], ShellToastKind.Success);
        NotifyCommandStatesChanged();
    }

    public void AddPendingImages(IEnumerable<ImageAttachment> images) =>
        ChatPage.AddPendingImages(images);

    private void RequestScrollToBottom() => _chatScroll.ScrollToBottom();

    private void RequestScrollToBottomImmediate() => _chatScroll.ScrollToBottomImmediate();

    private async void OnOlderMessagesRequested(object? sender, EventArgs e)
    {
        if (_olderHistoryLoadInProgress
            || _olderDisplayCursor is not { } cursor
            || _savedChatView is not { } chatView)
        {
            return;
        }

        _olderHistoryLoadInProgress = true;
        try
        {
            var sessionId = _displayedSessionId;
            var loadGeneration = Volatile.Read(ref _sessionLoadGeneration);
            var page = await _sessionNavigation.LoadOlderDisplayPageAsync(
                sessionId,
                cursor,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);
            if (!string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal)
                || !IsSessionLoadCurrent(loadGeneration))
            {
                return;
            }

            _olderDisplayCursor = page.OlderCursor;
            _runtime.SetOlderDisplayCursor(sessionId, _olderDisplayCursor);
            await _activeUi.PrependDisplayMessagesAsync(
                page.Messages,
                _olderDisplayCursor,
                _activeUi.ShowToolCalls,
                _olderDisplayCursor is not null).ConfigureAwait(true);
            _runtime.MarkHydrated(sessionId, _olderDisplayCursor);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Loading older chat history failed: {ex.Message}");
            await chatView.SetOlderMessagesAvailableAsync(_olderDisplayCursor is not null).ConfigureAwait(true);
        }
        finally
        {
            _olderHistoryLoadInProgress = false;
        }
    }

    private void WireSessionUsageUi(SessionTurnUiController ui)
    {
        var sessionId = _displayedSessionId;
        ui.OnUsageRecorded = snapshot =>
            RunOnUi(() =>
            {
                if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
                {
                    SessionUsageLine = SessionUsageFormatter.Format(snapshot);
                }
            });
        ui.OnContextBudgetUpdated = (budget, pressure) =>
            RunOnUi(() =>
            {
                if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
                {
                    ContextOccupancy.Apply(budget, pressure);
                }
            });
        ui.OnOverflowRetrySkipped = () =>
            RunOnUi(() =>
            {
                if (!string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
                {
                    return;
                }

                ContextOccupancy.ApplyOverflow();
                ShowShellToast(_loc["Chat_OverflowRetrySkipped"], ShellToastKind.Info);
            });
        SessionUsageLine = SessionUsageFormatter.Format(_sessionUsageAccumulator.Get(_displayedSessionId));
        RefreshContextOccupancy();
    }

    private static void UnwireSessionUsageUi(SessionTurnUiController ui)
    {
        ui.OnUsageRecorded = null;
        ui.OnContextBudgetUpdated = null;
        ui.OnOverflowRetrySkipped = null;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }

    private void RefreshContextOccupancy() =>
        _ = RunGuardedAsync(RefreshContextOccupancyAsync, "context occupancy refresh");

    private async Task RefreshContextOccupancyAsync()
    {
        // Token accounting needs the real message list; wait out a partial session load instead of
        // measuring an empty history.
        await EnsureDisplayedSessionReadyAsync().ConfigureAwait(true);

        var session = _session;
        var sessionId = session.Id;

        ContextBudgetSnapshot budget;
        try
        {
            // Never run PrepareForTurn / BuildRuntimeContext (sync-over-async) on the UI thread —
            // contributors use GetAwaiter().GetResult() and deadlock the WPF sync context.
            budget = await Task.Run(() => _compactionService.ComputeBudget(session)).ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        if (!string.Equals(_displayedSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        var pressure = ContextPressureEvaluator.Evaluate(
            budget,
            _appSettings.ContextCompaction.DynamicCompaction);
        ContextOccupancy.Apply(budget, pressure);
    }
}
