using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ContextSidebarViewModel : ObservableObject
{
    private readonly IAppPathProvider _paths;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly ISshWorkspaceClient _sshClient;

    public ContextSidebarViewModel(
        IAppPathProvider paths,
        IAgentSkillCatalog skillCatalog,
        IMcpRegistry mcpRegistry,
        AppSettings settings,
        ISshWorkspaceClient sshClient)
    {
        _paths = paths;
        _skillCatalog = skillCatalog;
        _mcpRegistry = mcpRegistry;
        _sshClient = sshClient;
        Refresh(settings);
    }

    public ObservableCollection<string> Skills { get; } = new();
    public ObservableCollection<McpSidebarServerViewModel> McpServers { get; } = new();
    public ObservableCollection<WorkspaceTreeNodeViewModel> WorkspaceTree { get; } = new();
    public string LocalModelStatus { get; set; } = "Local Model Active";

    public void Refresh(AppSettings settings)
    {
        _skillCatalog.Reload();
        Skills.Clear();

        if (_skillCatalog.Skills.Count == 0)
        {
            Skills.Add($"未安装技能 ({_paths.SkillsPath})");
        }
        else
        {
            foreach (var skill in _skillCatalog.Skills.OrderBy(skill => skill.Name, StringComparer.Ordinal))
            {
                var status = SkillFilter.IsEnabled(skill, settings) ? "●" : "○";
                Skills.Add($"{status} {skill.Name}");
            }
        }

        var expandedServers = McpServers
            .Where(server => server.IsExpanded)
            .Select(server => server.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        McpServers.Clear();
        if (settings.McpServers.Count == 0)
        {
            McpServers.Add(new McpSidebarServerViewModel("未配置 MCP 服务器", enabled: false, status: null));
        }
        else
        {
            var statuses = _mcpRegistry.GetStatuses().ToDictionary(status => status.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var server in settings.McpServers)
            {
                statuses.TryGetValue(server.Name, out var runtime);
                McpServers.Add(new McpSidebarServerViewModel(
                    server.Name,
                    server.Enabled,
                    runtime,
                    expandedServers.Contains(server.Name)));
            }
        }
    }

    private static readonly TimeSpan WorkspaceTreeDebounceInterval = TimeSpan.FromMilliseconds(400);

    private IReadOnlyList<string> _workspaceIgnorePatterns = Array.Empty<string>();
    private string? _pendingWorkspaceRootPath;
    private (string RootPath, string DisplayName, IReadOnlyList<SshEntry> Entries)? _pendingRemoteTree;
    private DispatcherTimer? _workspaceTreeDebounceTimer;

    public void RefreshWorkspaceTree(string? workspaceRootPath, IReadOnlyList<string> ignorePatterns)
    {
        _workspaceIgnorePatterns = ignorePatterns;
        _pendingWorkspaceRootPath = workspaceRootPath;
        _pendingRemoteTree = null;
        ScheduleWorkspaceTreeRefresh();
    }

    public void RefreshRemoteWorkspaceTree(
        string rootPath,
        string displayName,
        IReadOnlyList<SshEntry> entries,
        IReadOnlyList<string> ignorePatterns)
    {
        _workspaceIgnorePatterns = ignorePatterns;
        _pendingWorkspaceRootPath = rootPath;
        _pendingRemoteTree = (rootPath, displayName, entries);
        ScheduleWorkspaceTreeRefresh();
    }

    private void ScheduleWorkspaceTreeRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            FlushWorkspaceTreeRefresh();
            return;
        }

        if (_workspaceTreeDebounceTimer is null)
        {
            _workspaceTreeDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = WorkspaceTreeDebounceInterval
            };
            _workspaceTreeDebounceTimer.Tick += (_, _) =>
            {
                _workspaceTreeDebounceTimer?.Stop();
                FlushWorkspaceTreeRefresh();
            };
        }

        _workspaceTreeDebounceTimer.Stop();
        _workspaceTreeDebounceTimer.Start();
    }

    private void FlushWorkspaceTreeRefresh()
    {
        WorkspaceTree.Clear();
        if (_pendingRemoteTree is { } remote)
        {
            foreach (var node in WorkspaceTreeNodeViewModel.BuildRemoteTree(
                         remote.RootPath,
                         remote.DisplayName,
                         remote.Entries,
                         _workspaceIgnorePatterns))
            {
                WorkspaceTree.Add(node);
            }

            return;
        }

        foreach (var node in WorkspaceTreeNodeViewModel.BuildTree(_pendingWorkspaceRootPath, _workspaceIgnorePatterns))
        {
            WorkspaceTree.Add(node);
        }
    }

    public async Task ExpandWorkspaceTreeNodeAsync(WorkspaceTreeNodeViewModel node)
    {
        if (node.IsRemote)
        {
            await node.EnsureRemoteChildrenLoadedAsync(
                async (path, cancellationToken) =>
                {
                    var entries = new List<SshEntry>();
                    await foreach (var entry in _sshClient.ListAsync(path, cancellationToken).ConfigureAwait(false))
                    {
                        entries.Add(entry);
                    }

                    return entries;
                },
                _workspaceIgnorePatterns).ConfigureAwait(true);
            return;
        }

        node.EnsureChildrenLoaded(_workspaceIgnorePatterns);
    }
}
