using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IMcpRegistry _mcpRegistry;

    public SettingsViewModel(AppSettings settings, IMcpRegistry mcpRegistry)
    {
        Settings = settings;
        _mcpRegistry = mcpRegistry;
        foreach (var server in Settings.McpServers)
        {
            McpServers.Add(new McpServerItemViewModel(server, _mcpRegistry, OnMcpServerEnabledChanged));
        }

        SelectedMcpServer = McpServers.FirstOrDefault();
    }

    public event EventHandler? McpConfigurationChanged;

    private void OnMcpServerEnabledChanged() => McpConfigurationChanged?.Invoke(this, EventArgs.Empty);

    internal void RefreshRuntimeStates()
    {
        foreach (var server in McpServers)
        {
            server.RefreshRuntimeState();
        }
    }

    public AppSettings Settings { get; }
    public string[] Sections { get; } = { "Models", "MCP", "Skills", "Workspace", "Tool Permissions", "Appearance" };
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
}
