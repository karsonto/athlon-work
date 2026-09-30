using System.Collections.ObjectModel;
using System.IO;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Audio;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

/// <summary>MCP, skill and workspace editors.</summary>
public sealed partial class SettingsViewModel
{
    public sealed record TerminalShellOption(string Value, string DisplayName);

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
}
