using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public sealed class ChatMessageViewModel
{
    public ChatMessageViewModel(ChatMessage message)
    {
        Role = message.Role.ToString();
        Content = message.Content;
        CreatedAt = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
    }

    public string Role { get; }
    public string Content { get; }
    public string CreatedAt { get; }
}

public sealed partial class ContextSidebarViewModel : ObservableObject
{
    public ContextSidebarViewModel(AppSettings settings)
    {
        Refresh(settings);
    }

    [ObservableProperty]
    private string activeSkill = "No active skill";

    public ObservableCollection<string> NativeTools { get; } = new() { "file_list", "file_read", "file_write", "file_edit", "grep_files", "glob_files", "execute_command" };
    public ObservableCollection<string> McpServers { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();
    public string LocalModelStatus { get; set; } = "Local Model Active";

    public void Refresh(AppSettings settings)
    {
        ActiveSkill = settings.Skills.FirstOrDefault(skill => skill.Enabled)?.Name ?? "No active skill";

        McpServers.Clear();
        var servers = settings.McpServers.Count == 0
            ? new[] { "No MCP servers configured" }
            : settings.McpServers.Select(server => $"{(server.Enabled ? "●" : "○")} {server.Name}");

        foreach (var server in servers)
        {
            McpServers.Add(server);
        }

        RefreshWorkspaceFiles(settings);
    }

    public void RefreshWorkspaceFiles(AppSettings settings)
    {
        RecentFiles.Clear();
        var workspace = GetActiveWorkspace(settings);
        if (workspace is null || string.IsNullOrWhiteSpace(workspace.RootPath) || !Directory.Exists(workspace.RootPath))
        {
            RecentFiles.Add("No workspace configured");
            return;
        }

        var files = Directory.EnumerateFiles(workspace.RootPath, "*", SearchOption.TopDirectoryOnly)
            .Where(file => !ShouldIgnore(file, workspace.IgnorePatterns))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(12)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name));

        foreach (var file in files)
        {
            RecentFiles.Add(file!);
        }

        if (RecentFiles.Count == 0)
        {
            RecentFiles.Add("Workspace has no files");
        }
    }

    public static WorkspaceSettings? GetActiveWorkspace(AppSettings settings)
    {
        return settings.Workspaces.FirstOrDefault(workspace => workspace.IsDefault) ?? settings.Workspaces.FirstOrDefault();
    }

    private static bool ShouldIgnore(string file, IReadOnlyCollection<string> ignorePatterns)
    {
        var name = Path.GetFileName(file);
        return ignorePatterns.Any(pattern => string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed partial class McpServerItemViewModel : ObservableObject
{
    public McpServerItemViewModel(McpServerSettings settings)
    {
        Settings = settings;
    }

    public McpServerSettings Settings { get; }

    public string DisplayInitial => string.IsNullOrWhiteSpace(Name) ? "M" : Name.Trim()[0].ToString().ToUpperInvariant();

    public string Name
    {
        get => Settings.Name;
        set
        {
            if (Settings.Name == value)
            {
                return;
            }

            Settings.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayInitial));
        }
    }

    public bool Enabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled == value)
            {
                return;
            }

            Settings.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ToolSummary));
        }
    }

    public string Command
    {
        get => Settings.Command;
        set
        {
            if (Settings.Command == value)
            {
                return;
            }

            Settings.Command = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandSummary));
        }
    }

    public string ArgsText
    {
        get => string.Join(" ", Settings.Args);
        set
        {
            Settings.Args.Clear();
            if (!string.IsNullOrWhiteSpace(value))
            {
                Settings.Args.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandSummary));
        }
    }

    public string StatusText => Enabled ? "Configured and enabled" : "Configured but disabled";

    public string ToolSummary => Enabled ? "0 tools configured" : "Disabled";

    public string CommandSummary
    {
        get
        {
            var args = ArgsText;
            return string.IsNullOrWhiteSpace(args) ? $"command: {Command}" : $"command: {Command} {args}";
        }
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings)
    {
        Settings = settings;
        foreach (var server in Settings.McpServers)
        {
            McpServers.Add(new McpServerItemViewModel(server));
        }

        SelectedMcpServer = McpServers.FirstOrDefault();
    }

    public AppSettings Settings { get; }
    public string[] Sections { get; } = { "Models", "Logging", "MCP", "Skills", "Workspace", "Tool Permissions", "Appearance" };
    public ObservableCollection<McpServerItemViewModel> McpServers { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMcpServer))]
    [NotifyPropertyChangedFor(nameof(EditableMcpArgs))]
    private McpServerItemViewModel? selectedMcpServer;

    public bool HasSelectedMcpServer => SelectedMcpServer is not null;

    public SkillSettings EditableSkill
    {
        get
        {
            if (Settings.Skills.Count == 0)
            {
                Settings.Skills.Add(new SkillSettings());
            }

            return Settings.Skills[0];
        }
    }

    public McpServerSettings EditableMcpServer
    {
        get
        {
            if (SelectedMcpServer is null)
            {
                AddMcpServer();
            }

            return SelectedMcpServer!.Settings;
        }
    }

    public string EditableMcpArgs
    {
        get => SelectedMcpServer?.ArgsText ?? string.Empty;
        set
        {
            if (SelectedMcpServer is not null)
            {
                SelectedMcpServer.ArgsText = value;
            }
        }
    }

    [RelayCommand]
    private void AddMcpServer()
    {
        var nextIndex = Settings.McpServers.Count + 1;
        var server = new McpServerSettings
        {
            Name = $"custom-mcp-{nextIndex}",
            Command = "npx",
            Enabled = true
        };
        server.Args.Add("-y");

        Settings.McpServers.Add(server);
        var item = new McpServerItemViewModel(server);
        McpServers.Add(item);
        SelectedMcpServer = item;
    }

    [RelayCommand]
    private void SelectMcpServer(McpServerItemViewModel server)
    {
        SelectedMcpServer = server;
    }

    public WorkspaceSettings EditableWorkspace
    {
        get
        {
            if (Settings.Workspaces.Count == 0)
            {
                Settings.Workspaces.Add(new WorkspaceSettings());
            }

            return Settings.Workspaces[0];
        }
    }
}

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IFileStorageService _storage;
    private readonly ICredentialStore _credentialStore;
    private readonly AppSettings _appSettings;
    private FileSystemWatcher? _workspaceWatcher;
    private CancellationTokenSource? _turnCancellation;
    private AgentSession _session = AgentSession.Create("New Chat");

    public MainWindowViewModel(IAgentOrchestrator orchestrator, IFileStorageService storage, ICredentialStore credentialStore, AppSettings settings)
    {
        _orchestrator = orchestrator;
        _storage = storage;
        _credentialStore = credentialStore;
        _appSettings = settings;
        Settings = new SettingsViewModel(settings);
        Sidebar = new ContextSidebarViewModel(settings);
        HasStoredApiKey = EnsureCurrentApiKeySecret(settings);
        ActiveWorkspaceName = GetWorkspaceDisplayName(settings);
        ConfigureWorkspaceWatcher();
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ContextSidebarViewModel Sidebar { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private string composerText = string.Empty;

    public bool IsComposerEmpty => string.IsNullOrWhiteSpace(ComposerText);

    [ObservableProperty]
    private string streamingText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string currentPage = "Chat";

    [ObservableProperty]
    private string currentSessionTitle = "New Chat";

    [ObservableProperty]
    private string settingsStatus = "Settings are stored as JSON files under the app data folder.";

    [ObservableProperty]
    private string apiKey = string.Empty;

    [ObservableProperty]
    private bool hasStoredApiKey;

    [ObservableProperty]
    private string activeWorkspaceName = "No workspace";

    public bool IsChatPage => CurrentPage == "Chat";
    public bool IsSettingsPage => CurrentPage == "Settings";

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page;
    }

    [RelayCommand]
    private void NewSession()
    {
        _turnCancellation?.Cancel();
        _session = AgentSession.Create("New Chat");
        CurrentSessionTitle = _session.Title;
        ComposerText = string.Empty;
        StreamingText = string.Empty;
        IsBusy = false;
        Messages.Clear();
        CurrentPage = "Chat";
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposerText))
        {
            return;
        }

        var input = ComposerText.Trim();
        ComposerText = string.Empty;
        IsBusy = true;
        StreamingText = string.Empty;
        _turnCancellation = new CancellationTokenSource();
        Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, input)));

        try
        {
            var previousMessageCount = _session.Messages.Count;
            _session = await _orchestrator.SendAsync(_session, input, token =>
            {
                StreamingText += token;
                return Task.CompletedTask;
            }, _turnCancellation.Token);
            CurrentSessionTitle = _session.Title;
            foreach (var message in _session.Messages.Skip(previousMessageCount + 1))
            {
                Messages.Add(new ChatMessageViewModel(message));
            }
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, "生成已停止。")));
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.System, $"模型调用失败：{ex.Message}")));
        }
        finally
        {
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Stop() => _turnCancellation?.Cancel();

    [RelayCommand]
    private async Task ConfigureWorkspaceAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select agent workspace",
            Multiselect = false
        };

        var currentWorkspace = ContextSidebarViewModel.GetActiveWorkspace(_appSettings);
        if (currentWorkspace is not null && Directory.Exists(currentWorkspace.RootPath))
        {
            dialog.InitialDirectory = currentWorkspace.RootPath;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var workspace = Settings.EditableWorkspace;
        workspace.RootPath = dialog.FolderName;
        workspace.Name = new DirectoryInfo(dialog.FolderName).Name;
        workspace.IsDefault = true;
        ActiveWorkspaceName = GetWorkspaceDisplayName(_appSettings);

        await _storage.SaveSettingsAsync(_appSettings);
        Sidebar.Refresh(_appSettings);
        ConfigureWorkspaceWatcher();
        SettingsStatus = $"Workspace saved at {DateTime.Now:HH:mm:ss}";
    }

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

        Settings.Settings.Model.LegacyApiKeyCredentialName = null;
        await _storage.SaveSettingsAsync(Settings.Settings);
        Sidebar.Refresh(Settings.Settings);
        ActiveWorkspaceName = GetWorkspaceDisplayName(_appSettings);
        ConfigureWorkspaceWatcher();
        OnPropertyChanged(nameof(Sidebar));
        SettingsStatus = $"Saved at {DateTime.Now:HH:mm:ss}";
    }

    private bool CanSend() => !IsBusy;

    private void ConfigureWorkspaceWatcher()
    {
        _workspaceWatcher?.Dispose();
        _workspaceWatcher = null;

        var workspace = ContextSidebarViewModel.GetActiveWorkspace(_appSettings);
        if (workspace is null || string.IsNullOrWhiteSpace(workspace.RootPath) || !Directory.Exists(workspace.RootPath))
        {
            return;
        }

        _workspaceWatcher = new FileSystemWatcher(workspace.RootPath)
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _workspaceWatcher.Created += WorkspaceChanged;
        _workspaceWatcher.Deleted += WorkspaceChanged;
        _workspaceWatcher.Changed += WorkspaceChanged;
        _workspaceWatcher.Renamed += WorkspaceChanged;
    }

    private void WorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() => Sidebar.RefreshWorkspaceFiles(_appSettings));
    }

    private static string GetWorkspaceDisplayName(AppSettings settings)
    {
        var workspace = ContextSidebarViewModel.GetActiveWorkspace(settings);
        if (workspace is null || string.IsNullOrWhiteSpace(workspace.RootPath))
        {
            return "No workspace";
        }

        return string.IsNullOrWhiteSpace(workspace.Name) ? workspace.RootPath : workspace.Name;
    }

    public void Dispose()
    {
        _workspaceWatcher?.Dispose();
        _turnCancellation?.Dispose();
    }

    private bool EnsureCurrentApiKeySecret(AppSettings settings)
    {
        var hasCurrentSecret = _credentialStore.HasSecretAsync(ModelSettings.ApiKeySecretName).GetAwaiter().GetResult();
        if (hasCurrentSecret || string.IsNullOrWhiteSpace(settings.Model.LegacyApiKeyCredentialName))
        {
            return hasCurrentSecret;
        }

        var legacySecret = _credentialStore.GetSecretAsync(settings.Model.LegacyApiKeyCredentialName).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(legacySecret))
        {
            return false;
        }

        _credentialStore.SaveSecretAsync(ModelSettings.ApiKeySecretName, legacySecret).GetAwaiter().GetResult();
        return true;
    }

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    partial void OnComposerTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerEmpty));
        SendCommand.NotifyCanExecuteChanged();
    }
}
