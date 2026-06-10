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

public sealed partial class ContextSidebarViewModel : ObservableObject
{
    private readonly IAppPathProvider _paths;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IMcpRegistry _mcpRegistry;

    public ContextSidebarViewModel(IAppPathProvider paths, IAgentSkillCatalog skillCatalog, IMcpRegistry mcpRegistry, AppSettings settings)
    {
        _paths = paths;
        _skillCatalog = skillCatalog;
        _mcpRegistry = mcpRegistry;
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

        var disabled = new HashSet<string>(
            settings.Skills.Where(skill => !skill.Enabled).Select(skill => skill.Name),
            StringComparer.OrdinalIgnoreCase);

        if (_skillCatalog.Skills.Count == 0)
        {
            Skills.Add($"未安装技能 ({_paths.SkillsPath})");
        }
        else
        {
            foreach (var skill in _skillCatalog.Skills.OrderBy(skill => skill.Name, StringComparer.Ordinal))
            {
                var status = disabled.Contains(skill.Name) ? "○" : "●";
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
    private DispatcherTimer? _workspaceTreeDebounceTimer;

    public void RefreshWorkspaceTree(string? workspaceRootPath, IReadOnlyList<string> ignorePatterns)
    {
        _workspaceIgnorePatterns = ignorePatterns;
        _pendingWorkspaceRootPath = workspaceRootPath;
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
        foreach (var node in WorkspaceTreeNodeViewModel.BuildTree(_pendingWorkspaceRootPath, _workspaceIgnorePatterns))
        {
            WorkspaceTree.Add(node);
        }
    }

    public void ExpandWorkspaceTreeNode(WorkspaceTreeNodeViewModel node) =>
        node.EnsureChildrenLoaded(_workspaceIgnorePatterns);
}
