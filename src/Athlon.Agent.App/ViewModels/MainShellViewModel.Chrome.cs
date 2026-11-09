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
/// Shell chrome: navigation, SSO display, theme toggling, sidebar visibility/width, localized
/// strings and layout-state notifications. Split out of the shell orchestration file.
/// </summary>
public partial class MainShellViewModel
{
    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = AppPageExtensions.Parse(page);
    }

    /// <summary>Return to the page that navigated into the current page (falls back to Chat).</summary>
    [RelayCommand]
    private void NavigateBack()
    {
        var target = _pageBeforeCurrent;
        if (target == CurrentPage)
        {
            target = AppPage.Chat;
        }

        CurrentPage = target;
    }

    void INavigationService.Navigate(AppPage page) => CurrentPage = page;

    /// <summary>Chat / shell entry: open Trace popup for the latest Team task of the active session.</summary>
    [RelayCommand]
    private async Task OpenLatestTeamTraceAsync()
    {
        await TeamsPageVm.OpenLatestTraceAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Chat Teams mode send interceptor. Returns true when the composer submit was handled as a
    /// Team Task (including validation failures that should not fall through to a normal turn).
    /// </summary>
    private async Task<bool> TrySubmitTeamsGoalFromChatAsync(string goal)
    {
        if (ComposerHarness.SelectedMode != Athlon.Agent.Core.Harness.SessionAgentMode.Teams)
        {
            return false;
        }

        if (!_appSettings.Teams.Enabled)
        {
            ShowShellToast(_loc["Teams_DisabledToast"], ShellToastKind.Error);
            return true;
        }

        var team = ComposerHarness.SelectedTeam;
        if (team is null)
        {
            await ComposerHarness.RefreshTeamsAsync().ConfigureAwait(true);
            team = ComposerHarness.SelectedTeam;
        }

        if (team is null)
        {
            ShowShellToast(_loc["Teams_NoTeamToast"], ShellToastKind.Error);
            return true;
        }

        if (_sessionTurns.IsRunning(_displayedSessionId))
        {
            ShowShellToast(_loc["AskUser_BusyCannotSubmit"], ShellToastKind.Error);
            return true;
        }

        // Drop any pending ask for this session so a new goal cannot race with ResumeTeamAsk.
        if (TeamsPageVm.TryGetPendingAsk(_displayedSessionId, out _))
        {
            TeamsPageVm.ClearPendingAsk(_displayedSessionId);
            TeamsPageVm.CancelActiveTeamRun(_displayedSessionId);
        }

        await EnsureDisplayedSessionReadyAsync().ConfigureAwait(true);
        // Capture session + UI so a mid-run switch cannot write the summary into another chat.
        var captureSessionId = _displayedSessionId;
        var captureUi = _activeUi;
        ShowShellToast(_loc["Teams_RunningToast"], ShellToastKind.Info);
        if (string.Equals(_displayedSessionId, captureSessionId, StringComparison.Ordinal))
        {
            SetComposerStatus($"Teams · {team.Name}");
        }

        var userMessage = captureUi.AppendLocalUserMessage(goal);
        var turnAnchorId = userMessage.Id;
        await PersistTeamChatMessageAsync(captureSessionId, userMessage).ConfigureAwait(true);

        var progress = CreateTeamChatProgress(captureSessionId, captureUi, turnAnchorId);
        // Register CTS inside RunTeamGoal before first await — refresh Busy/sidebar after kickoff starts.
        RequestRefreshSessionHistory();

        try
        {
            // Run via orchestrator path without mutating Teams config page (no new Team / no list refresh).
            // Multiple sessions may run Teams concurrently; Busy is always derived from the displayed session.
            var runTask = TeamsPageVm.RunTeamGoalAsync(
                captureSessionId,
                goal,
                team.Id,
                openTrace: true,
                progress: progress);
            SyncSessionUiPin(captureSessionId);
            UpdateDisplayedBusyState();
            RequestRefreshSessionHistory();

            var (ok, message, task) = await runTask.ConfigureAwait(true);

            await ApplyTeamRunOutcomeToChatAsync(
                    captureSessionId,
                    captureUi,
                    ok,
                    message,
                    task,
                    turnAnchorId)
                .ConfigureAwait(true);
        }
        finally
        {
            SyncSessionUiPin(captureSessionId);
            if (string.Equals(_displayedSessionId, captureSessionId, StringComparison.Ordinal))
            {
                UpdateDisplayedBusyState();
                RefreshTeamComposerStatusForDisplayedSession();
            }

            RequestRefreshSessionHistory();
            NotifyCommandStatesChanged();
        }

        return true;
    }

    private IProgress<Athlon.Agent.Core.Teams.TeamRunProgress> CreateTeamChatProgress(
        string captureSessionId,
        SessionTurnUiController captureUi,
        string turnAnchorId) =>
        new Progress<Athlon.Agent.Core.Teams.TeamRunProgress>(step =>
        {
            _ = Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await ApplyTeamStepProgressToChatAsync(captureSessionId, captureUi, turnAnchorId, step)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.Warning("Failed to apply Team step progress to chat: {Error}", ex.Message);
                }
            });
        });

    private async Task ApplyTeamStepProgressToChatAsync(
        string captureSessionId,
        SessionTurnUiController captureUi,
        string turnAnchorId,
        Athlon.Agent.Core.Teams.TeamRunProgress step)
    {
        var markdown = TeamChatArtifacts.FormatStepMarkdown(step);
        var assistantMessage = captureUi.AppendLocalAssistantMessage(markdown);
        await PersistTeamChatMessageAsync(captureSessionId, assistantMessage).ConfigureAwait(true);

        // Kickoff Trace bubble has no file edits yet — skip FilesChanged churn.
        if (step.Kind == Athlon.Agent.Core.Teams.TeamRunProgressKind.TaskStarted)
        {
            return;
        }

        await PublishTeamFilesChangedAsync(
                captureSessionId,
                captureUi,
                turnAnchorId,
                step.RootSessionId,
                step.TaskId,
                seal: false)
            .ConfigureAwait(true);
    }

    private async Task PublishTeamFilesChangedAsync(
        string captureSessionId,
        SessionTurnUiController captureUi,
        string? turnAnchorId,
        string rootSessionId,
        string taskId,
        bool seal)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        var task = await TeamsPageVm
            .GetTaskSnapshotAsync(rootSessionId, taskId)
            .ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        var files = TeamsPageVm.CollectChatFiles(task);
        if (files.Count == 0 && !seal)
        {
            return;
        }

        FillMissingWriteDiffsFromWorkspace(files);
        // Keep file cards on the captured UI even when the user switched away mid-run.
        captureUi.PublishLocalFilesChanged(turnAnchorId, files, upsert: !seal);
    }

    private async Task ApplyTeamRunOutcomeToChatAsync(
        string captureSessionId,
        SessionTurnUiController captureUi,
        bool ok,
        string message,
        Athlon.Agent.Core.Teams.TeamTask? task,
        string? turnAnchorId = null)
    {
        string summaryMarkdown;
        if (string.Equals(message, "cancelled", StringComparison.OrdinalIgnoreCase)
            || task?.Status == Athlon.Agent.Core.Teams.TeamTaskStatus.Cancelled)
        {
            summaryMarkdown = FormatTeamsChatSummary(
                _loc["Teams_CancelledReason"],
                task?.TaskId,
                failed: true);
            ShowShellToast(_loc["Teams_CancelledToast"], ShellToastKind.Info);
        }
        else if (task?.Status == Athlon.Agent.Core.Teams.TeamTaskStatus.AwaitingHuman)
        {
            PushTeamAskUserQuestion(captureSessionId, task);
            summaryMarkdown = FormatTeamsChatSummary(
                _loc.Format("Teams_AwaitingHumanSummary", task.PendingHumanQuestion ?? message),
                task.TaskId,
                failed: false);
            ShowShellToast(_loc["Teams_AwaitingHumanToast"], ShellToastKind.Info);
        }
        else if (ok && task is not null)
        {
            var resultText = ResolveTeamsFinalResultText(task, message);
            // Steps already streamed full Worker/Leader replies; keep final bubble focused.
            var finalBody = string.IsNullOrWhiteSpace(resultText)
                ? "Teams run finished. See steps above for details."
                : resultText;
            summaryMarkdown = FormatTeamsChatSummary(finalBody, task.TaskId, failed: false);
            ShowShellToast(
                _loc.Format("Teams_CompletedToast", task.Status.ToString()),
                ShellToastKind.Success);
        }
        else
        {
            var failText = !string.IsNullOrWhiteSpace(task?.Error)
                ? task!.Error!
                : message;
            summaryMarkdown = FormatTeamsChatSummary(failText, task?.TaskId, failed: true);
            ShowShellToast(
                _loc.Format("Teams_FailedToast", failText),
                ShellToastKind.Error);
        }

        var assistantMessage = captureUi.AppendLocalAssistantMessage(summaryMarkdown);
        await PersistTeamChatMessageAsync(captureSessionId, assistantMessage).ConfigureAwait(true);
        if (string.Equals(_displayedSessionId, captureSessionId, StringComparison.Ordinal))
        {
            QuestionBar.RefreshFromActiveSession();
        }

        if (task is not null && !string.IsNullOrWhiteSpace(turnAnchorId))
        {
            await PublishTeamFilesChangedAsync(
                    captureSessionId,
                    captureUi,
                    turnAnchorId,
                    task.RootSessionId,
                    task.TaskId,
                    seal: true)
                .ConfigureAwait(true);
        }
    }

    private async Task PersistTeamChatMessageAsync(string sessionId, ChatMessage message)
    {
        if (string.Equals(_displayedSessionId, sessionId, StringComparison.Ordinal))
        {
            _session = _session.WithMessage(message);
            await SaveSessionInBackgroundAsync(_session).ConfigureAwait(true);
            return;
        }

        // Background Teams runs must still land in session.json so a later switch reload is complete.
        try
        {
            await _runtime.UpsertAsync(sessionId, message).ConfigureAwait(true);
            RequestRefreshSessionHistory();
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to persist Teams chat message for {SessionId}: {Error}", sessionId, ex.Message);
        }
    }

    private void SyncSessionUiPin(string sessionId) =>
        _uiCache.SetPinned(sessionId, IsSessionRunning(sessionId));

    private void RefreshTeamComposerStatusForDisplayedSession()
    {
        if (TeamsPageVm.IsTeamRunActive(_displayedSessionId))
        {
            SetComposerStatus("Teams · running");
            return;
        }

        if (!StatusFeedback.IsComposerStatusVisible
            || StatusFeedback.ComposerStatusText.StartsWith("Teams ·", StringComparison.Ordinal))
        {
            SetComposerStatus(null);
        }
    }

    private void PushTeamAskUserQuestion(string sessionId, Athlon.Agent.Core.Teams.TeamTask task)
    {
        var prompt = string.IsNullOrWhiteSpace(task.PendingHumanQuestion)
            ? "Team needs your input."
            : task.PendingHumanQuestion!;
        _userQuestions.SetPending(
            sessionId,
            new Athlon.Agent.Core.Plan.UserQuestion
            {
                RequestId = "team_" + task.TaskId,
                AllowFreeText = true,
                Questions =
                [
                    new Athlon.Agent.Core.Plan.UserQuestionItem
                    {
                        Id = "q1",
                        Prompt = prompt,
                        AllowMultiple = false,
                        Options = []
                    }
                ]
            });
    }

    private string FormatTeamsChatSummary(string body, string? taskId, bool failed)
    {
        var text = string.IsNullOrWhiteSpace(body) ? "(empty)" : body.Trim();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return failed
                ? _loc.Format("Teams_ChatSummaryFailNoTrace", text)
                : text;
        }

        return failed
            ? _loc.Format("Teams_ChatSummaryFail", text, taskId)
            : _loc.Format("Teams_ChatSummaryOk", text, taskId);
    }

    private static string ResolveTeamsFinalResultText(Athlon.Agent.Core.Teams.TeamTask task, string fallback)
    {
        if (task.Status == Athlon.Agent.Core.Teams.TeamTaskStatus.Succeeded
            && !string.IsNullOrWhiteSpace(task.FinalResult))
        {
            return task.FinalResult!;
        }

        if (!string.IsNullOrWhiteSpace(fallback)
            && !string.Equals(fallback, "Succeeded", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fallback, "Running", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fallback, "AwaitingHuman", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return string.IsNullOrWhiteSpace(task.Error) ? fallback : task.Error!;
    }

    private async void OnTeamTraceLinkRequested(object? sender, string uri)
    {
        try
        {
            if (!TryParseTeamTraceDeepLink(uri, out var taskId))
            {
                ShowShellToast(_loc["Teams_NoTeamToast"], ShellToastKind.Error);
                return;
            }

            await TeamsPageVm.OpenTraceByTaskIdAsync(_displayedSessionId, taskId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowShellToast(ex.Message, ShellToastKind.Error);
        }
    }

    private static bool TryParseTeamTraceDeepLink(string? uri, out string taskId)
    {
        taskId = "";
        if (string.IsNullOrWhiteSpace(uri)
            || !uri.StartsWith("athlon://team-trace", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // athlon://team-trace?taskId=tm_xxx  or athlon://team-trace/tm_xxx
        var queryIndex = uri.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            var query = uri[(queryIndex + 1)..];
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2
                    && string.Equals(kv[0], "taskId", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(kv[1]))
                {
                    taskId = Uri.UnescapeDataString(kv[1]);
                    return true;
                }
            }
        }

        var path = uri["athlon://team-trace".Length..].TrimStart('/', '?');
        if (!string.IsNullOrWhiteSpace(path) && !path.Contains('=', StringComparison.Ordinal))
        {
            taskId = Uri.UnescapeDataString(path);
            return true;
        }

        return false;
    }

    [RelayCommand]
    private void SsoLogout()
    {
        if (!_navigation.TryConfirmSsoLogout())
        {
            return;
        }

        _navigation.ClearSsoSession();
        Application.Current.Shutdown(0);
    }

    private void InitializeSsoDisplay()
    {
        var (displayName, isVisible) = _navigation.GetSsoDisplayState();
        SsoDisplayName = displayName;
        IsSsoUserVisible = isVisible;
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        var next = AppThemeManager.CurrentKind == AppThemeKind.Light
            ? AppThemeKind.Dark
            : AppThemeKind.Light;
        AppThemeManager.SetTheme(next, _appSettings.Ui);
        NotifyThemeToggleStateChanged();
        await _layout.PersistNowAsync();
    }

    [RelayCommand]
    private async Task ToggleContextSidebarAsync()
    {
        SetContextSidebarVisible(!_appSettings.Ui.ContextSidebarVisible, animate: true);
        await _layout.PersistNowAsync();
    }

    [RelayCommand]
    private async Task ToggleToolsNavExpandedAsync()
    {
        _appSettings.Ui.ToolsNavExpanded = !_appSettings.Ui.ToolsNavExpanded;
        OnPropertyChanged(nameof(IsToolsNavExpanded));
        OnPropertyChanged(nameof(ToolsNavExpandGlyph));
        await _layout.PersistNowAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleWorkspaceMaximized()
    {
        if (IsWorkspaceMaximized)
        {
            RestoreWorkspaceMaximized();
            return;
        }

        if (!WorkspacePane.CanMaximizeActiveTab)
        {
            return;
        }

        if (!IsContextSidebarVisible)
        {
            SetContextSidebarVisible(true, animate: false);
        }

        _preMaximizeContextWidth = ContextSidebarWidth;
        IsWorkspaceMaximized = true;
    }

    public void RestoreWorkspaceMaximized()
    {
        if (!IsWorkspaceMaximized)
        {
            return;
        }

        IsWorkspaceMaximized = false;
        if (_preMaximizeContextWidth >= ContextSidebarMinWidth)
        {
            UpdateContextSidebarWidth(_preMaximizeContextWidth);
        }
    }

    private void OnWorkspacePanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(WorkspacePaneViewModel.ActiveTab)
            or nameof(WorkspacePaneViewModel.CanMaximizeActiveTab)
            or nameof(WorkspacePaneViewModel.IsEmpty)))
        {
            return;
        }

        if (IsWorkspaceMaximized && !WorkspacePane.CanMaximizeActiveTab)
        {
            RestoreWorkspaceMaximized();
        }
    }

    partial void OnIsWorkspaceMaximizedChanged(bool value) =>
        OnPropertyChanged(nameof(WorkspaceMaximizeToolTip));

    [RelayCommand]
    private void StartComputerUseOverlay()
    {
        if (IsComputerUseOverlayActive)
        {
            return;
        }

        WorkspacePane.IsAddMenuOpen = false;
        IsComputerUseOverlayActive = true;
    }

    public void EndComputerUseOverlay()
    {
        if (!IsComputerUseOverlayActive)
        {
            return;
        }

        IsComputerUseOverlayActive = false;
    }

    partial void OnIsComputerUseOverlayActiveChanged(bool value)
    {
        if (value)
        {
            AttachComputerUseStatusMessageListeners();
        }
        else
        {
            DetachComputerUseStatusMessageListeners();
        }

        RefreshComputerUseStatus();
    }

    [RelayCommand]
    private void OpenChangesWorkspaceTab()
    {
        WorkspacePane.IsAddMenuOpen = false;
        var existing = WorkspacePane.Tabs.OfType<ChangesWorkspaceTabViewModel>().FirstOrDefault();
        if (existing is not null)
        {
            WorkspacePane.ActiveTab = existing;
            return;
        }

        var review = _activeUi.GetLatestReviewFiles();
        FillMissingWriteDiffsFromWorkspace(review.Files);
        WorkspacePane.ShowChanges(review.Files, null, review.EntryId);
    }

    [RelayCommand]
    private async Task OpenBrowserWorkspaceTabAsync()
    {
        if (!_appSettings.Ui.ContextSidebarVisible)
        {
            SetContextSidebarVisible(true, animate: true);
            await _layout.PersistNowAsync().ConfigureAwait(true);
        }

        WorkspacePane.AddBrowserTabCommand.Execute(null);
    }

    [RelayCommand]
    private async Task OpenAthlonWebAsync()
    {
        try
        {
            var url = await _athlonWebServer.EnsureStartedAsync().ConfigureAwait(true);
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _notifier.Warning("Shell_AthlonWeb", "Shell_AthlonWebStartFailed", exception.Message);
        }
    }

    /// <summary>Opens an http(s) URL from chat markdown links in a right-side Browser workspace tab.</summary>
    public async Task OpenChatLinkInBrowserAsync(string url)
    {
        var normalized = BrowserWorkspaceTabViewModel.NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!_appSettings.Ui.ContextSidebarVisible)
        {
            SetContextSidebarVisible(true, animate: true);
            await _layout.PersistNowAsync().ConfigureAwait(true);
        }

        WorkspacePane.OpenUrlInBrowserTab(normalized);
    }

    [RelayCommand]
    private async Task ToggleNavigationSidebarAsync()
    {
        SetNavigationSidebarVisible(!_appSettings.Ui.NavigationSidebarVisible, animate: true);
        await _layout.PersistNowAsync();
    }

    private void OnCultureChanged(object? sender, EventArgs e) => RefreshLocalizedStrings();

    private void RefreshLocalizedStrings()
    {
        ShutdownStatusText = _loc["Shell_ShuttingDown"];
        OnPropertyChanged(nameof(ThemeToggleToolTip));
        OnPropertyChanged(nameof(ContextSidebarToggleToolTip));
        OnPropertyChanged(nameof(NavigationSidebarToggleToolTip));
        if (string.IsNullOrWhiteSpace(_session.ActiveWorkspace))
        {
            ActiveWorkspaceName = _loc["Shell_NoWorkspace"];
        }

        OnPropertyChanged(nameof(HasSessionWorkspace));
        OnPropertyChanged(nameof(WorkspacePanelActionLabel));
        OnPropertyChanged(nameof(ChatWelcomeTitle));
        OnPropertyChanged(nameof(ChatWelcomeDescription));
        OnPropertyChanged(nameof(SidebarAccountTitle));
        OnPropertyChanged(nameof(SidebarAccountSubtitle));
        OnPropertyChanged(nameof(SsoAvatarInitial));
    }

    partial void OnSsoDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(ChatWelcomeTitle));
        OnPropertyChanged(nameof(SsoAvatarInitial));
        OnPropertyChanged(nameof(SidebarAccountTitle));
        OnPropertyChanged(nameof(SidebarAccountSubtitle));
    }

    partial void OnIsSsoUserVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarAccountTitle));
        OnPropertyChanged(nameof(SidebarAccountSubtitle));
        OnPropertyChanged(nameof(SsoAvatarInitial));
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        NotifyThemeToggleStateChanged();
        foreach (var group in AgentRecordGroups)
        {
            group.NotifyThemeBrushesChanged();
        }
    }

    private void NotifyThemeToggleStateChanged()
    {
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ThemeToggleToolTip));
    }

    public void SetContextSidebarVisible(bool visible, bool animate = false)
    {
        if (!visible && IsWorkspaceMaximized)
        {
            RestoreWorkspaceMaximized();
        }

        _contextSidebarLayoutAnimate = animate;
        _layout.SetContextSidebarVisible(visible, NotifyContextSidebarLayoutChanged);
    }

    public void SetNavigationSidebarVisible(bool visible, bool animate = false)
    {
        _navigationSidebarLayoutAnimate = animate;
        _layout.SetNavigationSidebarVisible(visible, NotifyNavigationSidebarLayoutChanged);
    }

    internal void SetContextSidebarEdgeGutterWidth(double width)
    {
        if (Math.Abs(_contextSidebarEdgeGutterWidth - width) < 0.01)
        {
            return;
        }

        _contextSidebarEdgeGutterWidth = width;
        OnPropertyChanged(nameof(ContextSidebarEdgeGutterWidth));
    }

    public void UpdateComposerHeight(double height)
    {
        _layout.UpdateComposerHeight(height);
        OnPropertyChanged(nameof(ComposerHeight));
    }

    public void UpdateContextSidebarWidth(double width) =>
        _layout.UpdateContextSidebarWidth(width);

    public void UpdateNavigationSidebarWidth(double width) =>
        _layout.UpdateNavigationSidebarWidth(width);

    /// <summary>??WebChatView ????????UI ???????????????/summary>
    public void AttachChatView(Controls.WebChatView chatView)
    {
        if (_savedChatView is not null)
        {
            _savedChatView.OlderMessagesRequested -= OnOlderMessagesRequested;
            _savedChatView.ExternalLinkRequested -= OnChatExternalLinkRequested;
            _savedChatView.TeamTraceLinkRequested -= OnTeamTraceLinkRequested;
            _savedChatView.ForkChatRequested -= OnForkChatRequested;
            _savedChatView.ReportDownloadRequested -= OnReportDownloadRequested;
            _savedChatView.ToolDetailRequested -= OnToolDetailRequested;
            _planSurface.DetachChatView(_savedChatView);
            _savedChatView.ReviewFilesRequested -= OnReviewFilesRequested;
        }

        _savedChatView = chatView;
        chatView.TeamTraceLinkRequested += OnTeamTraceLinkRequested;
        chatView.OlderMessagesRequested += OnOlderMessagesRequested;
        chatView.ExternalLinkRequested += OnChatExternalLinkRequested;
        chatView.ForkChatRequested += OnForkChatRequested;
        chatView.ReportDownloadRequested += OnReportDownloadRequested;
        chatView.ToolDetailRequested += OnToolDetailRequested;
        _planSurface.AttachChatView(chatView);
        chatView.ReviewFilesRequested += OnReviewFilesRequested;
        _uiCache.AttachChatViewToAll(chatView);
        _activeUi.ChatView = chatView;
        _ = _activeUi.ReloadChatViewAsync();
        _ = chatView.SetOlderMessagesAvailableAsync(_olderDisplayCursor is not null);
    }

    private async void OnChatExternalLinkRequested(object? sender, string url)
    {
        try
        {
            await OpenChatLinkInBrowserAsync(url).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Open chat link in browser failed: {ex.Message}");
            ShowShellToast(ex.Message, ShellToastKind.Error);
        }
    }

    private void OnReviewFilesRequested(object? sender, Controls.ReviewFilesRequestedEventArgs e)
    {
        var files = _activeUi.GetTurnFiles(e.EntryId);
        if (files.Count == 0)
        {
            // Cold Review after reload: rebuild from the Teams task linked in this turn.
            files = TryCollectTeamFilesForReview(e.EntryId);
        }

        FillMissingWriteDiffsFromWorkspace(files);
        WorkspacePane.ShowChanges(files, e.Path, e.EntryId);
        if (!IsContextSidebarVisible)
        {
            SetContextSidebarVisible(true, animate: true);
        }
    }

    private IReadOnlyList<ModifiedFileViewModel> TryCollectTeamFilesForReview(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId) || string.IsNullOrWhiteSpace(_session.Id))
        {
            return Array.Empty<ModifiedFileViewModel>();
        }

        var anchor = entryId.StartsWith("files:", StringComparison.Ordinal)
            ? entryId["files:".Length..]
            : entryId;
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return Array.Empty<ModifiedFileViewModel>();
        }

        var start = -1;
        for (var i = 0; i < _session.Messages.Count; i++)
        {
            if (string.Equals(_session.Messages[i].Id, anchor, StringComparison.Ordinal))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return Array.Empty<ModifiedFileViewModel>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ModifiedFileViewModel>();
        for (var i = start + 1; i < _session.Messages.Count; i++)
        {
            var message = _session.Messages[i];
            if (message.Role is MessageRole.User or MessageRole.Compaction)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(message.Content)
                || !TeamChatArtifacts.TryParseTeamTaskIdFromContent(message.Content, out var taskId)
                || !seen.Add(taskId))
            {
                continue;
            }

            files.AddRange(TeamChatArtifacts.TryCollectTaskFilesForReplay(_session.Id, taskId));
        }

        var merged = SessionModifiedFilesTracker.MergeByPath(files);
        if (merged.Count > 0)
        {
            _activeUi.RememberPublishedTurnFiles(entryId, merged);
        }

        return merged;
    }

    /// <summary>
    /// A file_write that did not keep a diff still has the file on disk. Show that text as additions
    /// so Review is not an empty "no diff" pane.
    /// </summary>
    private void FillMissingWriteDiffsFromWorkspace(IReadOnlyList<ModifiedFileViewModel> files)
    {
        if (_workspaceContext.Kind == WorkspaceKind.Ssh)
        {
            return;
        }

        var workspaceRoot = !string.IsNullOrWhiteSpace(_session.ActiveWorkspace)
            ? _session.ActiveWorkspace
            : _workspaceContext.RootPath;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        var root = Path.GetFullPath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            if (file.HasDiff || string.IsNullOrWhiteSpace(file.RelativePath))
            {
                continue;
            }

            var combined = Path.GetFullPath(Path.Combine(
                root,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(combined))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(combined);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                file.SetDiff(UnifiedDiffGenerator.Generate(string.Empty, text, file.RelativePath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async void OnToolDetailRequested(object? sender, Controls.ToolDetailRequestEventArgs e)
    {
        var chatView = _savedChatView;
        if (chatView is null)
        {
            return;
        }

        try
        {
            var sessionId = _displayedSessionId;
            var detail = await ToolDetailReadback.LoadDisplayDetailAsync(
                _storage,
                sessionId,
                e.MessageId,
                e.ToolCallId,
                CancellationToken.None).ConfigureAwait(true);
            if (!string.Equals(sessionId, _displayedSessionId, StringComparison.Ordinal))
            {
                return;
            }

            await chatView.PostToolDetailAsync(
                e.RequestId,
                e.MessageId,
                e.ToolCallId,
                detail ?? string.Empty).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Tool detail readback failed: {ex.Message}");
            await chatView.PostToolDetailAsync(
                e.RequestId,
                e.MessageId,
                e.ToolCallId,
                string.Empty).ConfigureAwait(true);
        }
    }

    private void NotifyContextSidebarLayoutChanged()
    {
        var animate = _contextSidebarLayoutAnimate;
        _contextSidebarLayoutAnimate = false;
        if (!animate)
        {
            SetContextSidebarEdgeGutterWidth(0);
        }

        OnPropertyChanged(nameof(IsContextSidebarVisible));
        OnPropertyChanged(nameof(ContextSidebarWidth));
        OnPropertyChanged(nameof(ContextSidebarToggleToolTip));
        ContextSidebarLayoutChanged?.Invoke(
            this,
            new ContextSidebarLayoutChangedEventArgs { Animate = animate });
    }

    private void NotifyNavigationSidebarLayoutChanged()
    {
        var animate = _navigationSidebarLayoutAnimate;
        _navigationSidebarLayoutAnimate = false;

        OnPropertyChanged(nameof(IsNavigationSidebarVisible));
        OnPropertyChanged(nameof(NavigationSidebarWidth));
        OnPropertyChanged(nameof(NavigationSidebarToggleToolTip));
        NavigationSidebarLayoutChanged?.Invoke(
            this,
            new ContextSidebarLayoutChangedEventArgs { Animate = animate });
    }

    public Task PersistUiLayoutForSidebarAsync() => _layout.PersistNowAsync();
}
