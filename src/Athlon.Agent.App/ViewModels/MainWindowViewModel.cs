using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _appSettings;
    private readonly IImpSsoSessionStore? _ssoSessionStore;
    private readonly UiLayoutSettingsBridge _uiLayout;
    private readonly SessionHistoryCoordinator _sessionHistory;
    private readonly WorkspaceSessionBridge _workspaceBridge = new();
    private readonly ApplicationShutdownService _shutdownService;
    private readonly SchedulerService _scheduler;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IAppPathProvider _paths;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private ICredentialStore _credentialStore => Chat.CredentialStore;
    private readonly IFileStorageService _storage;

    public ChatViewModel Chat { get; }
    public SettingsViewModel Settings { get; }
    public ScheduleViewModel SchedulePageVm { get; }
    public ContextSidebarViewModel Sidebar { get; }
    public FileEditorViewModel FileEditor { get; }
    public string LogsPath { get; }

    // === Delegation to ChatViewModel for XAML/code-behind compatibility ===
    public void ShowCopyNotice(string msg) => Chat.ShowCopyNotice(msg);
    public void AddPendingImages(IEnumerable<ImageAttachment> images) => Chat.AddPendingImages(images);
    public void UpdateComposerCompletion(string text, int caretIndex) => Chat.UpdateComposerCompletion(text, caretIndex);
    public bool TryAcceptAtCompletion(int caretIndex, out int newCaretIndex) => Chat.TryAcceptAtCompletion(caretIndex, out newCaretIndex);
    public void MoveAtCompletionSelection(int delta) => Chat.MoveAtCompletionSelection(delta);
    public void CloseAtCompletion() => Chat.CloseAtCompletion();
    public IRelayCommand SendCommand => Chat.SendCommand;
    public IRelayCommand ClearContextCommand => Chat.ClearContextCommand;
    public IRelayCommand NewSessionCommand => Chat.NewSessionCommand;
    public IRelayCommand StopCommand => Chat.StopCommand;

    // === Keep properties for XAML backward compatibility ===
    // These delegate to ChatViewModel so XAML bindings don't need to change.
    public ObservableCollection<ChatMessageViewModel> Messages => Chat.Messages;
    public bool HasChatMessages => Messages.Count > 0;
    public bool IsBusy => Chat.IsBusy;
    public bool IsLoadingSession => Chat.IsLoadingSession;
    public string ComposerText { get => Chat.ComposerText; set => Chat.ComposerText = value; }
    public bool IsComposerEmpty => Chat.IsComposerEmpty;
    public bool HasPendingImages => Chat.HasPendingImages;
    public string CurrentSessionTitle { get => Chat.CurrentSessionTitle; set => Chat.CurrentSessionTitle = value; }
    public string SessionUsageLine { get => Chat.SessionUsageLine; set => Chat.SessionUsageLine = value; }
    public string SettingsStatus { get => Chat.SettingsStatus; set => Chat.SettingsStatus = value; }
    public bool IsAtCompletionOpen { get => Chat.IsAtCompletionOpen; set => Chat.IsAtCompletionOpen = value; }
    public int SelectedAtCompletionIndex { get => Chat.SelectedAtCompletionIndex; set => Chat.SelectedAtCompletionIndex = value; }
    public bool IsCopyNoticeVisible { get => Chat.IsCopyNoticeVisible; set => Chat.IsCopyNoticeVisible = value; }
    public string CopyNotice { get => Chat.CopyNotice; set => Chat.CopyNotice = value; }
    public ObservableCollection<AtCompletionItemViewModel> AtCompletionItems => Chat.AtCompletionItems;
    public ObservableCollection<PendingImageAttachmentViewModel> PendingImageAttachments => Chat.PendingImageAttachments;
    public ObservableCollection<QueuedTurnViewModel> QueuedTurns => Chat.QueuedTurns;
    public bool HasQueuedTurns => Chat.HasQueuedTurns;
    public string ApiKey { get => Chat.ApiKey; set => Chat.ApiKey = value; }
    public bool HasStoredApiKey { get => Chat.HasStoredApiKey; set => Chat.HasStoredApiKey = value; }
    public string KnowledgeEmbeddingApiKey { get => Chat.KnowledgeEmbeddingApiKey; set => Chat.KnowledgeEmbeddingApiKey = value; }
    public bool HasStoredKnowledgeEmbeddingApiKey { get => Chat.HasStoredKnowledgeEmbeddingApiKey; set => Chat.HasStoredKnowledgeEmbeddingApiKey = value; }
    public string ActiveWorkspaceName { get => Chat.ActiveWorkspaceName; set => Chat.ActiveWorkspaceName = value; }
    public KnowledgeViewModel KnowledgePageVm => Chat.KnowledgePageVm;
    public ComposerKnowledgeViewModel ComposerKnowledge => Chat.ComposerKnowledge;
    public Action? ScrollChatToBottom { get => Chat.ScrollChatToBottom; set => Chat.ScrollChatToBottom = value; }
    public Action? ScrollChatToBottomImmediate { get => Chat.ScrollChatToBottomImmediate; set => Chat.ScrollChatToBottomImmediate = value; }
    public ObservableCollection<AgentRecordGroupViewModel> AgentRecordGroups => Chat.AgentRecordGroups;
    public bool HasAgentRecords => Chat.HasAgentRecords;
    public string ShutdownStatusText { get => Chat.ShutdownStatusText; set => Chat.ShutdownStatusText = value; }
    public bool HasPendingShutdownWork => Chat.HasPendingShutdownWork;

    // === Theme ===
    public bool IsLightTheme => AppThemeManager.CurrentKind == AppThemeKind.Light;
    public string ThemeToggleGlyph => IsLightTheme ? "☾" : "☀";
    public string ThemeToggleToolTip => IsLightTheme ? "切换到深色模式" : "切换到浅色模式";

    // === Layout Constants ===
    public const double ContextSidebarMinWidth = 220;
    public const double ContextSidebarMaxWidth = 600;
    public const double ContextSidebarDefaultWidth = 340;
    public const double ContextSidebarCollapseDragThreshold = 100;
    public const double NavigationSidebarMinWidth = 180;
    public const double NavigationSidebarMaxWidth = 400;
    public const double NavigationSidebarDefaultWidth = 240;
    public const double EditorPaneMinWidth = 200;
    public const double EditorPaneMaxWidth = 1000;
    public const double EditorPaneDefaultWidth = 500;
    public const double ComposerMinHeight = 48;
    public const double ComposerMaxHeight = 400;
    public const double ComposerDefaultHeight = 120;

    public double EditorPaneWidth => Math.Clamp(_appSettings.Ui.EditorPaneWidth, EditorPaneMinWidth, EditorPaneMaxWidth);
    public double NavigationSidebarWidth => Math.Clamp(_appSettings.Ui.NavigationSidebarWidth, NavigationSidebarMinWidth, NavigationSidebarMaxWidth);
    public double ComposerHeight => Math.Clamp(_appSettings.Ui.ComposerHeight, ComposerMinHeight, ComposerMaxHeight);
    public bool IsContextSidebarVisible => _appSettings.Ui.ContextSidebarVisible;
    public double ContextSidebarWidth => Math.Clamp(_appSettings.Ui.ContextSidebarWidth, ContextSidebarMinWidth, ContextSidebarMaxWidth);
    public string ContextSidebarToggleToolTip => IsContextSidebarVisible ? "关闭右侧栏 (Ctrl+Alt+B)" : "打开右侧栏 (Ctrl+Alt+B)";
    public event EventHandler? ContextSidebarLayoutChanged;

    // === Page Navigation ===
    [ObservableProperty] private string currentPage = "Chat";
    public bool IsChatPage => CurrentPage == "Chat";
    public bool IsSettingsPage => CurrentPage == "Settings";
    public bool IsSchedulePage => CurrentPage == "Schedule";
    public bool IsKnowledgePage => CurrentPage == "Knowledge";

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsSchedulePage));
        OnPropertyChanged(nameof(IsKnowledgePage));
        if (string.Equals(value, "Settings", StringComparison.Ordinal)) Settings.SyncSkillsFromCatalog();
        else if (string.Equals(value, "Schedule", StringComparison.Ordinal)) SchedulePageVm.SyncFromSettings();
        else if (string.Equals(value, "Knowledge", StringComparison.Ordinal)) _ = Chat.KnowledgePageVm.RefreshIfStaleAsync();
    }

    [RelayCommand] private void Navigate(string page) { CurrentPage = page; if (string.Equals(page, "Settings", StringComparison.Ordinal)) Settings.SyncSkillsFromCatalog(); }

    // === SSO ===
    [ObservableProperty] private string ssoDisplayName = string.Empty;
    [ObservableProperty] private bool isSsoUserVisible;

    private void InitializeSsoDisplay()
    {
        if (!_appSettings.Sso.Enabled || _ssoSessionStore is null) { SsoDisplayName = string.Empty; IsSsoUserVisible = false; return; }
        var session = _ssoSessionStore.GetCachedSession();
        if (session is null || _ssoSessionStore.IsExpired(session)) { SsoDisplayName = string.Empty; IsSsoUserVisible = false; return; }
        SsoDisplayName = session.DisplayName;
        IsSsoUserVisible = !string.IsNullOrWhiteSpace(SsoDisplayName);
    }

    [RelayCommand]
    private void SsoLogout()
    {
        if (!_appSettings.Sso.Enabled) return;
        var result = MessageBox.Show("确定要退出登录吗？", AppVersionInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _ssoSessionStore?.Clear();
        Application.Current.Shutdown(0);
    }

    // === Theme ===
    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        var next = AppThemeManager.CurrentKind == AppThemeKind.Light ? AppThemeKind.Dark : AppThemeKind.Light;
        AppThemeManager.SetTheme(next, _appSettings.Ui);
        NotifyThemeToggleStateChanged();
        await _uiLayout.PersistNowAsync();
    }

    [RelayCommand]
    private async Task ToggleContextSidebarAsync()
    {
        SetContextSidebarVisible(!_appSettings.Ui.ContextSidebarVisible);
        await _uiLayout.PersistNowAsync();
    }

    private void OnAppThemeChanged(object? sender, EventArgs e) => NotifyThemeToggleStateChanged();

    private void NotifyThemeToggleStateChanged()
    {
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ThemeToggleGlyph));
        OnPropertyChanged(nameof(ThemeToggleToolTip));
    }

    public void SetContextSidebarVisible(bool visible)
    {
        var ui = _appSettings.Ui;
        if (ui.ContextSidebarVisible == visible) return;
        ui.ContextSidebarVisible = visible;
        if (visible && ui.ContextSidebarWidth < ContextSidebarMinWidth) ui.ContextSidebarWidth = ContextSidebarDefaultWidth;
        NotifyContextSidebarLayoutChanged();
    }

    public void UpdateContextSidebarWidth(double width) => TryUpdateDimension(_appSettings.Ui.ContextSidebarWidth, width, ContextSidebarMinWidth, ContextSidebarMaxWidth, v => _appSettings.Ui.ContextSidebarWidth = v);
    public void UpdateNavigationSidebarWidth(double width) => TryUpdateDimension(_appSettings.Ui.NavigationSidebarWidth, width, NavigationSidebarMinWidth, NavigationSidebarMaxWidth, v => _appSettings.Ui.NavigationSidebarWidth = v);
    public void UpdateComposerHeight(double height) => TryUpdateDimension(_appSettings.Ui.ComposerHeight, height, ComposerMinHeight, ComposerMaxHeight, v => _appSettings.Ui.ComposerHeight = v);
    public void UpdateEditorPaneWidth(double width) => TryUpdateDimension(_appSettings.Ui.EditorPaneWidth, width, EditorPaneMinWidth, EditorPaneMaxWidth, v => _appSettings.Ui.EditorPaneWidth = v);

    private void TryUpdateDimension(double current, double value, double min, double max, Action<double> setter)
    {
        if (!_appSettings.Ui.ContextSidebarVisible) return;
        _uiLayout.TryUpdateDimension(current, value, min, max, setter);
    }

    private void NotifyContextSidebarLayoutChanged()
    {
        OnPropertyChanged(nameof(IsContextSidebarVisible));
        OnPropertyChanged(nameof(ContextSidebarWidth));
        OnPropertyChanged(nameof(ContextSidebarToggleToolTip));
        ContextSidebarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task PersistUiLayoutForSidebarAsync() => _uiLayout.PersistNowAsync();

    // === Settings ===
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            await _credentialStore.SaveSecretAsync(ModelSettings.ApiKeySecretName, ApiKey);
            ApiKey = string.Empty;
            HasStoredApiKey = true;
            OnPropertyChanged(nameof(ApiKey));
        }
        if (!string.IsNullOrWhiteSpace(KnowledgeEmbeddingApiKey))
        {
            await _credentialStore.SaveSecretAsync(KnowledgeEmbeddingSettings.ApiKeySecretName, KnowledgeEmbeddingApiKey);
            KnowledgeEmbeddingApiKey = string.Empty;
            HasStoredKnowledgeEmbeddingApiKey = true;
            ComposerKnowledge.SetEmbeddingApiKeyAvailable(true);
            OnPropertyChanged(nameof(KnowledgeEmbeddingApiKey));
        }
        Settings.Settings.Model.LegacyApiKeyCredentialName = null;
        SettingsViewModel.PruneEmptyWorkspaces(Settings.Settings);
        Settings.SyncSkillsFromCatalog();
        await _storage.SaveSettingsAsync(Settings.Settings);
        await RefreshMcpRuntimeAsync();
        Chat.ApplySessionWorkspace();
        OnPropertyChanged(nameof(Sidebar));
        SettingsStatus = $"Saved at {AppTimeZone.Now:HH:mm:ss}";
        CurrentPage = "Chat";
    }

    [RelayCommand]
    private async Task ConfigureWorkspaceAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Select agent workspace", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(Chat.Session.ActiveWorkspace) && Directory.Exists(Chat.Session.ActiveWorkspace))
            dialog.InitialDirectory = Chat.Session.ActiveWorkspace;
        if (dialog.ShowDialog() != true) return;
        var folderName = new DirectoryInfo(dialog.FolderName).Name;
        Chat.SetSessionWorkspace(dialog.FolderName);
        await Chat.SaveCurrentSessionIfNeededAsync();
        SettingsStatus = $"当前对话工作区：{folderName}";
    }

    // === MCP ===
    private async Task RefreshMcpRuntimeAsync()
    {
        await _mcpRegistry.RefreshAsync(Settings.Settings.McpServers);
        Settings.RefreshRuntimeStates();
        Sidebar.Refresh(Settings.Settings);
        Chat.RefreshAtCompletionSources();
        OnPropertyChanged(nameof(Sidebar));
    }

    private void OnSkillConfigurationChanged()
    {
        _skillCatalog.Reload();
        Sidebar.Refresh(_appSettings);
        Chat.RefreshAtCompletionSources(reloadSkills: true);
        OnPropertyChanged(nameof(Sidebar));
    }

    // === File Editor ===
    public bool HasOpenEditorTabs => FileEditor.HasOpenTabs;

    [RelayCommand(CanExecute = nameof(CanOpenWorkspaceTreeNodeInEditor))]
    private async Task OpenWorkspaceTreeNodeInEditorAsync(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanOpenWorkspaceTreeNodeInEditor(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath)) return;
        await FileEditor.OpenFileAsync(node.FullPath, Chat.Session.ActiveWorkspace).ConfigureAwait(true);
    }

    private bool CanOpenWorkspaceTreeNodeInEditor(WorkspaceTreeNodeViewModel? node) =>
        node is not null && !node.IsPlaceholder && !node.IsExpanderPlaceholder && !node.IsDirectory && !string.IsNullOrWhiteSpace(node.FullPath);

    [RelayCommand(CanExecute = nameof(CanOpenWorkspaceTreeNodeInExplorer))]
    private void OpenWorkspaceTreeNodeInExplorer(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanOpenWorkspaceTreeNodeInExplorer(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath)) return;
        try
        {
            var fullPath = Path.GetFullPath(node.FullPath);
            var targetPath = node.IsDirectory ? fullPath : Path.GetDirectoryName(fullPath);
            if (targetPath is null) return;
            Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法打开文件夹：{exception.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool CanOpenWorkspaceTreeNodeInExplorer(WorkspaceTreeNodeViewModel? node) =>
        node is not null && !node.IsPlaceholder && !node.IsExpanderPlaceholder && !string.IsNullOrWhiteSpace(node.FullPath);

    [RelayCommand] private async Task SaveActiveEditorAsync() { if (FileEditor.ActiveDocument is null) return; await FileEditor.SaveDocumentAsync(FileEditor.ActiveDocument).ConfigureAwait(true); }
    [RelayCommand] private void CloseEditorTab(EditorDocumentViewModel? document) => FileEditor.CloseTabCommand.Execute(document);
    public bool ConfirmCloseEditorTabs() => FileEditor.TryCloseAllTabs();

    [RelayCommand(CanExecute = nameof(CanDeleteWorkspaceItem))]
    private void DeleteWorkspaceItem(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanDeleteWorkspaceItem(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath)) return;
        var path = Path.GetFullPath(node.FullPath);
        var kind = node.IsDirectory ? "文件夹" : "文件";
        var prompt = node.IsDirectory ? $"确定删除{kind}「{node.Name}」及其全部内容吗？此操作无法撤销。" : $"确定删除{kind}「{node.Name}」吗？此操作无法撤销。";
        if (MessageBox.Show(prompt, "删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (node.IsDirectory) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            else if (File.Exists(path)) File.Delete(path);
            else { SettingsStatus = "目标不存在或已被删除。"; Sidebar.RefreshWorkspaceTree(Chat.Session.ActiveWorkspace, _workspaceContext.IgnorePatterns); return; }
            Chat.RefreshAtCompletionSources();
            Sidebar.RefreshWorkspaceTree(Chat.Session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
            SettingsStatus = $"已删除{kind}「{node.Name}」。";
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法删除「{node.Name}」：{exception.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            SettingsStatus = $"删除失败：{exception.Message}";
        }
    }

    private bool CanDeleteWorkspaceItem(WorkspaceTreeNodeViewModel? node) =>
        node is not null && !node.IsPlaceholder && !node.IsExpanderPlaceholder && !string.IsNullOrWhiteSpace(node.FullPath)
        && WorkspaceSessionBridge.TryGetActiveWorkspaceRoot(Chat.Session, out var root)
        && WorkspaceSessionBridge.IsPathUnderWorkspace(root, node.FullPath)
        && !WorkspaceSessionBridge.IsWorkspaceRootPath(root, node.FullPath);

    // === Session History ===
    [RelayCommand] private async Task LoadSessionAsync(SessionHistoryItemViewModel? item) => await Chat.LoadSessionAsync(item);
    [RelayCommand] private async Task DeleteSessionAsync(SessionHistoryItemViewModel? item) => await Chat.DeleteSessionAsync(item);
    public async Task OpenSessionByIdAsync(string sessionId) => await Chat.OpenSessionByIdAsync(sessionId);
    private async Task SaveCurrentSessionIfNeededAsync() => await Chat.SaveCurrentSessionIfNeededAsync();

    // === Shutdown ===
    public async Task ShutdownAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _workspaceBridge.Dispose();
        await _shutdownService.ShutdownAsync(progress, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        Chat.Dispose();
        _uiLayout.Dispose();
        _sessionHistory.Dispose();
        _workspaceBridge.Dispose();
    }

    public MainWindowViewModel(
        AppSettings settings,
        IImpSsoSessionStore ssoSessionStore,
        IMcpRegistry mcpRegistry,
        IAgentSkillCatalog skillCatalog,
        IAppPathProvider paths,
        IActiveWorkspaceContext workspaceContext,
        IFileStorageService storage,
        ApplicationShutdownService shutdownService,
        SchedulerService scheduler,
        WorkspaceFileEditorService workspaceFileEditorService,
        ChatViewModel chatViewModel)
    {
        _appSettings = settings;
        _ssoSessionStore = settings.Sso.Enabled ? ssoSessionStore : null;
        _mcpRegistry = mcpRegistry;
        _skillCatalog = skillCatalog;
        _paths = paths;
        _workspaceContext = workspaceContext;
        _storage = storage;
        _shutdownService = shutdownService;
        _scheduler = scheduler;
        Chat = chatViewModel;
        _uiLayout = new UiLayoutSettingsBridge(storage, settings);
        _sessionHistory = new SessionHistoryCoordinator(storage);

        Settings = new SettingsViewModel(settings, _mcpRegistry, skillCatalog, paths);
        SchedulePageVm = new ScheduleViewModel(settings, storage, scheduler, OpenSessionByIdAsync);
        Sidebar = new ContextSidebarViewModel(paths, skillCatalog, _mcpRegistry, settings);
        FileEditor = new FileEditorViewModel(workspaceFileEditorService);
        FileEditor.Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOpenEditorTabs));
        FileEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileEditorViewModel.ActiveDocument) or nameof(FileEditorViewModel.HasOpenTabs))
            {
                OnPropertyChanged(nameof(HasOpenEditorTabs));
            }
        };
        Settings.McpConfigurationChanged += async (_, _) => await RefreshMcpRuntimeAsync();
        Settings.SkillConfigurationChanged += (_, _) => OnSkillConfigurationChanged();
        _uiLayout.ClampInitialLayout();
        LogsPath = paths.LogsPath;
        InitializeSsoDisplay();
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
    }
}