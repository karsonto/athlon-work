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

    void INavigationService.Navigate(AppPage page) => CurrentPage = page;

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

    private void OnAppThemeChanged(object? sender, EventArgs e) =>
        NotifyThemeToggleStateChanged();

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
            _savedChatView.ToolDetailRequested -= OnToolDetailRequested;
            _savedChatView.PlanBuildRequested -= OnPlanBuildRequested;
            _savedChatView.PlanReviseRequested -= OnPlanReviseRequested;
        }

        _savedChatView = chatView;
        chatView.OlderMessagesRequested += OnOlderMessagesRequested;
        chatView.ExternalLinkRequested += OnChatExternalLinkRequested;
        chatView.ToolDetailRequested += OnToolDetailRequested;
        chatView.PlanBuildRequested += OnPlanBuildRequested;
        chatView.PlanReviseRequested += OnPlanReviseRequested;
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
