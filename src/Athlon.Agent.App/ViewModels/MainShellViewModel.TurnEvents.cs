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
            _planSurface.OnEnteredPlanMode();
        }

        QuestionBar.RefreshFromActiveSession();
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
