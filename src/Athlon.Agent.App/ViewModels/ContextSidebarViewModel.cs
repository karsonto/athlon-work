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
    public ObservableCollection<string> McpServers { get; } = new();
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

        McpServers.Clear();
        if (settings.McpServers.Count == 0)
        {
            McpServers.Add("未配置 MCP 服务器");
        }
        else
        {
            var statuses = _mcpRegistry.GetStatuses().ToDictionary(status => status.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var server in settings.McpServers)
            {
                var status = server.Enabled ? "●" : "○";
                var command = string.IsNullOrWhiteSpace(server.Command) ? string.Empty : $"  {server.Command}";
                if (server.Enabled && statuses.TryGetValue(server.Name, out var runtime))
                {
                    var runtimeText = McpRuntimeStatusText.SidebarSummary(runtime);
                    McpServers.Add($"{status} {server.Name}  {runtimeText}");
                }
                else
                {
                    McpServers.Add($"{status} {server.Name}{command}");
                }
            }
        }
    }

    public void RefreshWorkspaceTree(string? workspaceRootPath, IReadOnlyList<string> ignorePatterns)
    {
        WorkspaceTree.Clear();
        foreach (var node in WorkspaceTreeNodeViewModel.BuildTree(workspaceRootPath, ignorePatterns))
        {
            WorkspaceTree.Add(node);
        }
    }
}
