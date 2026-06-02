using System.Collections.ObjectModel;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IMcpRegistry _mcpRegistry;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IAppPathProvider _paths;

    public SettingsViewModel(
        AppSettings settings,
        IMcpRegistry mcpRegistry,
        IAgentSkillCatalog skillCatalog,
        IAppPathProvider paths)
    {
        Settings = settings;
        _mcpRegistry = mcpRegistry;
        _skillCatalog = skillCatalog;
        _paths = paths;
        foreach (var server in Settings.McpServers)
        {
            McpServers.Add(new McpServerItemViewModel(server, _mcpRegistry, OnMcpServerEnabledChanged));
        }

        SelectedMcpServer = McpServers.FirstOrDefault();
        SyncSkillsFromCatalog();
    }

    public event EventHandler? McpConfigurationChanged;
    public event EventHandler? SkillConfigurationChanged;

    private void OnMcpServerEnabledChanged() => McpConfigurationChanged?.Invoke(this, EventArgs.Empty);

    private void OnSkillEnabledChanged() => SkillConfigurationChanged?.Invoke(this, EventArgs.Empty);

    internal void RefreshRuntimeStates()
    {
        foreach (var server in McpServers)
        {
            server.RefreshRuntimeState();
        }
    }

    public void SyncSkillsFromCatalog()
    {
        _skillCatalog.Reload();
        var installed = _skillCatalog.Skills;
        var installedNames = installed.Select(skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = SkillSettingsMerger.Merge(_paths.SkillsPath, installed, Settings.Skills);
        Settings.Skills.Clear();
        Settings.Skills.AddRange(merged);

        Skills.Clear();
        foreach (var settings in merged.OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            var description = installed.FirstOrDefault(skill =>
                string.Equals(skill.Name, settings.Name, StringComparison.OrdinalIgnoreCase))?.Description
                ?? string.Empty;
            var isInstalled = installedNames.Contains(settings.Name);
            Skills.Add(new SkillItemViewModel(settings, description, isInstalled, OnSkillEnabledChanged));
        }
    }

    public AppSettings Settings { get; }
    public string McpConfigPath => McpConfigFileService.GetPath(_paths);
    public string SkillsDirectoryPath => _paths.SkillsPath;
    public string SkillsConfigPath => SkillConfigFileService.GetPath(_paths);
    public string SkillsSettingsDescription =>
        $"技能从 {SkillsDirectoryPath} 自动加载；此页面控制每个技能是否启用。关闭后不会出现在系统提示与 @skill 补全中。保存设置后写入 {SkillsConfigPath}。";
    public string[] Sections { get; } = { "Models", "MCP", "Skills", "Workspace", "Tool Permissions", "Appearance" };
    public ObservableCollection<McpServerItemViewModel> McpServers { get; } = new();
    public ObservableCollection<SkillItemViewModel> Skills { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMcpServer))]
    [NotifyPropertyChangedFor(nameof(EditableMcpArgs))]
    private McpServerItemViewModel? selectedMcpServer;

    public bool HasSelectedMcpServer => SelectedMcpServer is not null;

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
        var item = new McpServerItemViewModel(server, _mcpRegistry, OnMcpServerEnabledChanged);
        McpServers.Add(item);
        SelectedMcpServer = item;
    }

    [RelayCommand]
    private void SelectMcpServer(McpServerItemViewModel server)
    {
        SelectedMcpServer = server;
    }

    [RelayCommand]
    private void DeleteMcpServer(McpServerItemViewModel? server)
    {
        if (server is null)
        {
            return;
        }

        Settings.McpServers.Remove(server.Settings);
        McpServers.Remove(server);
        if (ReferenceEquals(SelectedMcpServer, server))
        {
            SelectedMcpServer = McpServers.FirstOrDefault();
        }

        McpConfigurationChanged?.Invoke(this, EventArgs.Empty);
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

    internal static void PruneEmptyWorkspaces(AppSettings settings) =>
        settings.Workspaces.RemoveAll(workspace => string.IsNullOrWhiteSpace(workspace.RootPath));

    public string IgnoreDirectoriesText
    {
        get => string.Join(Environment.NewLine, Settings.WorkspaceIgnore.DirectoryNames);
        set => Settings.WorkspaceIgnore.DirectoryNames = ParseIgnoreDirectoryLines(value);
    }

    private static List<string> ParseIgnoreDirectoryLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
