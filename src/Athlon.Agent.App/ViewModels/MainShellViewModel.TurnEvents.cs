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
/// Turn/plan event handlers and mode changes that drive the shell surface: turn state, task
/// lists, plan cards and approved-plan starts. Split out of the shell orchestration file.
/// </summary>
public partial class MainShellViewModel
{
    private void OnTurnStateChanged(object? sender, string sessionId)
    {
        // Pin the controller for the duration of the turn so LRU eviction cannot drop its buffered
        // streaming state when the user switches away and back.
        _uiCache.SetPinned(sessionId, _sessionTurns.TurnHost.IsRunning(sessionId));
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                UpdateDisplayedBusyState();
            }

            RequestRefreshSessionHistory();
        });
    }

    private void OnKnowledgeDataChanged()
    {
        _ = ComposerKnowledge.LoadForSessionAsync(_displayedSessionId);
        _ = ComposerHarness.LoadForSessionAsync(_displayedSessionId);
    }

    private void OnTaskListChanged(string sessionId)
    {
        // Deleting a session clears its tasks, which fires this notification. Refreshing here would
        // re-read (and, for a missing directory, re-create) the files being deleted right before
        // Directory.Delete runs, so the delete is muted for that window.
        if (_suppressTaskListRefresh)
        {
            return;
        }

        if (!string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
        {
            return;
        }

        Application.Current?.Dispatcher.InvokeAsync(() => _ = ComposerHarness.RefreshTasksAsync());
    }

    /// <summary>
    /// Persists the task panel's auto-continue toggle. It lives in app settings rather than the
    /// session, so it saves with the rest of the settings and survives restarts.
    /// </summary>
    public async Task OnAutoContinueSettingChangedAsync()
    {
        try
        {
            await _storage.SaveSettingsAsync(_appSettings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowShellToast(ex.Message, ShellToastKind.Error);
        }
    }

    private async Task OnComposerModeChangedAsync(SessionAgentMode from, SessionAgentMode to)
    {
        if (from == SessionAgentMode.Debug && to != SessionAgentMode.Debug)
        {
            await DebugBar.AbandonActiveRunAsync().ConfigureAwait(true);
        }

        if (to == SessionAgentMode.Plan)
        {
            PlanBar.RefreshFromActiveRun();
            RefreshPlanCard();
        }

        QuestionBar.RefreshFromActiveSession();
    }

    /// <summary>
    /// Re-emits the plan card for the displayed session. A plan card is not backed by the
    /// transcript, so the timeline cannot restore it on its own; this is the single refresh path
    /// that keeps it pinned to the run's <see cref="TimelineOrderPolicy.Plan"/> slot.
    /// </summary>
    private void RefreshPlanCard()
    {
        var run = PlanBar.GetActiveRun();
        if (run is { Phase: PlanPhase.AwaitConfirm or PlanPhase.Done })
        {
            _activeUi.ShowPlanReady(run);
        }
        else
        {
            _activeUi.ClearPlanReady();
        }
    }

    /// <summary>
    /// The user submitted an <c>ask_user</c> answer in the QuestionBar. The formatted
    /// answer is sent as the next turn's user input: Plan resumes the AwaitClarify run,
    /// any other mode just continues the conversation.
    /// </summary>
    private void OnUserQuestionAnswered(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // If starting the turn fails (e.g. busy), SessionTurnCoordinator leaves the
        // pending question intact so the bar stays visible for a retry.
        _ = RunGuardedAsync(
            () => ChatPage.TrySubmitPlanInputAsync(text),
            "answer user question");
    }

    private void OnPlanBuildRequested(object? sender, EventArgs e)
    {
        if (PlanBar.BuildCommand.CanExecute(null))
        {
            PlanBar.BuildCommand.Execute(null);
        }
    }

    private void OnPlanReviseRequested(object? sender, EventArgs e)
    {
        if (!PlanBar.EnterReviseMode())
        {
            ShowShellToast(_loc["Plan_BuildMissingPlan"], ShellToastKind.Error);
        }
    }

    private async Task StartFromApprovedPlanAsync()
    {
        await EnsureDisplayedSessionReadyAsync().ConfigureAwait(true);
        var sessionId = _displayedSessionId;
        // The plan run lives in memory only and is the single source for this build.
        var approved = await _planRunStore.LoadActiveAsync(sessionId).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(approved?.PlanMarkdown))
        {
            ShowShellToast(_loc["Plan_BuildMissingPlan"], ShellToastKind.Error);
            return;
        }

        // Persist the plan before anything else can fail: from here on the plan must survive
        // compaction and session switches, which is what ApprovedPlanRuntimeContributor reads.
        await _planArtifactStore
            .SaveAsync(sessionId, approved.PlanMarkdown, approved)
            .ConfigureAwait(true);

        // Seed with merge=true so a plan built on top of an existing list (for example a re-Build
        // after a revision) updates items by id instead of silently replacing unrelated work.
        // Existing items keep their status: re-building a plan must not reopen work already done.
        var existingById = (await _taskListStore.GetAsync(sessionId).ConfigureAwait(true))
            .Items
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var seeds = approved.Todos
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Content))
            .Select(t => new AgentTaskItem
            {
                Id = t.Id,
                Content = t.Content,
                Status = existingById.TryGetValue(t.Id, out var existing)
                    ? existing.Status
                    : AgentTaskStatuses.Pending
            })
            .ToList();
        if (seeds.Count > 0)
        {
            await _taskListStore.ApplyMergeAsync(sessionId, seeds, merge: true).ConfigureAwait(true);
            _taskListChangedNotifier.Notify(sessionId);
        }

        // A fresh Build restarts the auto-continuation budget (including a previous manual stop).
        _planContinuationTracker.Reset(sessionId);

        // Switching modes before starting the turn matters: SessionTurnHost picks the Plan
        // orchestrator while the harness state still says Plan.
        if (ComposerHarness.SelectModeCommand.CanExecute(SessionAgentMode.Agent))
        {
            await ComposerHarness.SelectModeCommand.ExecuteAsync(SessionAgentMode.Agent).ConfigureAwait(true);
        }

        var ui = _sessionTurns.GetOrCreateUi(
            sessionId,
            _chatScroll.ScrollToBottom,
            _chatScroll.ScrollToBottomImmediate);
        ui.ResetForTurn();
        // The plan travels as a regular user message so it is persisted and replayed like any
        // other turn; ui.AddUserMessage is intentionally skipped because the UI hides this
        // control message instead of showing a user bubble.
        var error = _sessionTurns.TryStartTurn(
            sessionId,
            _session,
            BuildApprovedPlanMessage(approved.PlanMarkdown, seeds.Count > 0),
            Array.Empty<ImageAttachment>(),
            ui,
            appendUserMessage: true);
        if (error is not null)
        {
            ShowShellToast(error, ShellToastKind.Error);
            return;
        }

        // Consume the in-memory run only: the durable copy on disk is what keeps the plan in
        // context for the rest of the session, and the timeline card is replaced by the
        // execution state (the approved-plan message is hidden in the transcript).
        await PlanBar.ClearActiveRunAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the hidden approved-plan message, appending the merge guidance the model needs to
    /// update a pre-seeded list instead of overwriting it.
    /// </summary>
    private static string BuildApprovedPlanMessage(string markdown, bool taskListSeeded)
    {
        var message = ApprovedPlanPrompt.BuildUserMessage(markdown);
        return taskListSeeded
            ? message
              + "\n\nThe session task list has already been seeded from this plan's steps. "
              + "Update it with todo_write using merge=true (update items by id) and keep exactly one item in_progress. "
              + "Do not use merge=false — that would discard the seeded list."
            : message;
    }

    private void OnTurnCompleted(object? sender, SessionTurnCompletedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                _session = e.Session;
                CurrentSessionTitle = _session.Title;
                UpdateDisplayedBusyState();
                _ = ComposerHarness.RefreshTasksAsync();
                DebugBar.RefreshFromActiveRun();
                PlanBar.RefreshFromActiveRun();
                QuestionBar.RefreshFromActiveSession();
                // The ring only updates from streaming pushes during a turn; recompute once here so
                // the settled value reflects the final history (and any compaction that ran).
                RefreshContextOccupancy();
            }

            // Cache the finished session so switching back re-reads only the display tail, but
            // never cache an empty result (a session with no messages must reload from disk).
            if (e.Session.Messages.Count > 0)
            {
                _sessionNavigation.UpdateCachedSession(e.Session);
            }
            else
            {
                _sessionNavigation.Invalidate(e.SessionId);
            }

            _sessionNavigation.InvalidateDisplayPage(e.SessionId);
            _uiCache.SetPinned(e.SessionId, _sessionTurns.TurnHost.IsRunning(e.SessionId));
            RequestRefreshSessionHistory();
            if (_sessionTurns.QueuedTurnPresenter.TryProcessNext(e, out var queueError))
            {
                if (string.Equals(e.SessionId, _displayedSessionId, StringComparison.Ordinal))
                {
                    UpdateDisplayedBusyState();
                    if (queueError is not null)
                    {
                        ShowShellToast(queueError, ShellToastKind.Error);
                    }
                }
            }

            NotifyCommandStatesChanged();
        });
    }
}
