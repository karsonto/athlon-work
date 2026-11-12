using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public enum SkillsPanelSection
{
    Skills,
    Mcp
}

public enum SkillSidebarFilterMode
{
    All,
    Enabled,
    Disabled
}

public sealed partial class ContextSidebarViewModel : ObservableObject
{
    private readonly IAppPathProvider _paths;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly ISshWorkspaceClient _sshClient;
    private Func<string, Task>? _onSkillActivate;
    private Func<string, Task>? _onMcpActivate;
    private Action<string?>? _openSkillsFolder;
    private Action? _navigateToSkillHub;
    private bool _suppressFilterRebuild;

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

    public ObservableCollection<SkillSidebarItemViewModel> Skills { get; } = new();

    public ObservableCollection<SkillSidebarItemViewModel> FilteredSkills { get; } = new();

    public ObservableCollection<McpSidebarServerViewModel> McpServers { get; } = new();

    public ObservableCollection<McpSidebarServerViewModel> FilteredMcpServers { get; } = new();

    public ObservableCollection<WorkspaceTreeNodeViewModel> WorkspaceTree { get; } = new();

    public string LocalModelStatus { get; set; } = "Local Model Active";

    public string SkillsDirectoryPath => _paths.SkillsPath;

    [ObservableProperty]
    private bool hasInstalledSkills;

    [ObservableProperty]
    private string skillsEmptyMessage = string.Empty;

    [ObservableProperty]
    private bool hasConfiguredMcpServers;

    [ObservableProperty]
    private string mcpEmptyMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkillsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsMcpSectionSelected))]
    private SkillsPanelSection selectedSection = SkillsPanelSection.Skills;

    public bool IsSkillsSectionSelected => SelectedSection == SkillsPanelSection.Skills;

    public bool IsMcpSectionSelected => SelectedSection == SkillsPanelSection.Mcp;

    [ObservableProperty]
    private string skillSearchText = string.Empty;

    [ObservableProperty]
    private SkillSidebarFilterMode skillFilterMode = SkillSidebarFilterMode.All;

    [ObservableProperty]
    private string mcpSearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSkill))]
    private SkillSidebarItemViewModel? selectedSkill;

    public bool HasSelectedSkill => SelectedSkill is not null;

    [ObservableProperty]
    private int installedSkillCount;

    [ObservableProperty]
    private int enabledSkillCount;

    [ObservableProperty]
    private string skillsCountLabel = string.Empty;

    [ObservableProperty]
    private string skillsSectionLabel = string.Empty;

    [ObservableProperty]
    private int configuredMcpCount;

    [ObservableProperty]
    private int connectedMcpCount;

    [ObservableProperty]
    private string mcpCountLabel = string.Empty;

    [ObservableProperty]
    private string mcpSectionLabel = string.Empty;

    [ObservableProperty]
    private bool hasFilteredSkills;

    [ObservableProperty]
    private bool hasFilteredMcpServers;

    [ObservableProperty]
    private bool isSkillFilterAll = true;

    [ObservableProperty]
    private bool isSkillFilterEnabled;

    [ObservableProperty]
    private bool isSkillFilterDisabled;

    public void SetActivateHandlers(Func<string, Task> onSkillActivate, Func<string, Task> onMcpActivate)
    {
        _onSkillActivate = onSkillActivate;
        _onMcpActivate = onMcpActivate;
    }

    public void SetNavigationHandlers(Action<string?> openSkillsFolder, Action navigateToSkillHub)
    {
        _openSkillsFolder = openSkillsFolder;
        _navigateToSkillHub = navigateToSkillHub;
    }

    public void ApplySkillEnabled(string skillName, bool enabled)
    {
        var item = Skills.FirstOrDefault(skill =>
            string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        item.SetEnabled(enabled);
        RefreshSkillCounts();
        RebuildFilteredSkills(preserveSelection: true);
    }

    public void Refresh(AppSettings settings)
    {
        var selectedName = SelectedSkill?.Name;
        _suppressFilterRebuild = true;
        try
        {
            _skillCatalog.Reload();
            Skills.Clear();

            if (_skillCatalog.Skills.Count == 0)
            {
                HasInstalledSkills = false;
                SkillsEmptyMessage = Strings.Get("Context_SkillsEmpty");
                SelectedSkill = null;
            }
            else
            {
                HasInstalledSkills = true;
                SkillsEmptyMessage = string.Empty;
                foreach (var skill in _skillCatalog.Skills.OrderBy(skill => skill.Name, StringComparer.Ordinal))
                {
                    Skills.Add(new SkillSidebarItemViewModel(
                        skill.Name,
                        skill.Description,
                        skill.SkillDirectory,
                        SkillFilter.IsEnabled(skill, settings),
                        _onSkillActivate));
                }
            }

            var expandedServers = McpServers
                .Where(server => server.IsExpanded)
                .Select(server => server.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            McpServers.Clear();
            if (settings.McpServers.Count == 0)
            {
                HasConfiguredMcpServers = false;
                McpEmptyMessage = Strings.Get("Context_McpEmpty");
            }
            else
            {
                HasConfiguredMcpServers = true;
                McpEmptyMessage = string.Empty;
                var statuses = _mcpRegistry.GetStatuses()
                    .ToDictionary(status => status.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var server in settings.McpServers)
                {
                    statuses.TryGetValue(server.Name, out var runtime);
                    McpServers.Add(new McpSidebarServerViewModel(
                        server.Name,
                        server.Enabled,
                        runtime,
                        expandedServers.Contains(server.Name),
                        _onMcpActivate));
                }
            }
        }
        finally
        {
            _suppressFilterRebuild = false;
        }

        RefreshSkillCounts();
        RefreshMcpCounts();
        RebuildFilteredSkills(preserveSelection: false);
        RebuildFilteredMcpServers();

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            SelectedSkill = FilteredSkills.FirstOrDefault(skill =>
                string.Equals(skill.Name, selectedName, StringComparison.OrdinalIgnoreCase))
                ?? Skills.FirstOrDefault(skill =>
                    string.Equals(skill.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        }
        else if (SelectedSkill is null && FilteredSkills.Count > 0)
        {
            SelectedSkill = FilteredSkills[0];
        }

        SyncFilterModeFlags();
    }

    [RelayCommand]
    private void ShowSkillsSection() => SelectedSection = SkillsPanelSection.Skills;

    [RelayCommand]
    private void ShowMcpSection() => SelectedSection = SkillsPanelSection.Mcp;

    [RelayCommand]
    private void SetSkillFilterAll() => SkillFilterMode = SkillSidebarFilterMode.All;

    [RelayCommand]
    private void SetSkillFilterEnabled() => SkillFilterMode = SkillSidebarFilterMode.Enabled;

    [RelayCommand]
    private void SetSkillFilterDisabled() => SkillFilterMode = SkillSidebarFilterMode.Disabled;

    [RelayCommand]
    private void OpenSkillsFolder()
    {
        _openSkillsFolder?.Invoke(null);
    }

    [RelayCommand]
    private void OpenSelectedSkillFolder()
    {
        _openSkillsFolder?.Invoke(SelectedSkill?.DirectoryPath);
    }

    [RelayCommand]
    private void NavigateToSkillHub()
    {
        _navigateToSkillHub?.Invoke();
    }

    partial void OnSkillSearchTextChanged(string value)
    {
        if (!_suppressFilterRebuild)
        {
            RebuildFilteredSkills(preserveSelection: true);
        }
    }

    partial void OnSkillFilterModeChanged(SkillSidebarFilterMode value)
    {
        SyncFilterModeFlags();
        if (!_suppressFilterRebuild)
        {
            RebuildFilteredSkills(preserveSelection: true);
        }
    }

    partial void OnMcpSearchTextChanged(string value)
    {
        if (!_suppressFilterRebuild)
        {
            RebuildFilteredMcpServers();
        }
    }

    private void SyncFilterModeFlags()
    {
        IsSkillFilterAll = SkillFilterMode == SkillSidebarFilterMode.All;
        IsSkillFilterEnabled = SkillFilterMode == SkillSidebarFilterMode.Enabled;
        IsSkillFilterDisabled = SkillFilterMode == SkillSidebarFilterMode.Disabled;
    }

    private void RefreshSkillCounts()
    {
        InstalledSkillCount = Skills.Count;
        EnabledSkillCount = Skills.Count(skill => skill.IsEnabled);
        SkillsCountLabel = Strings.Format("Context_SkillsCount", EnabledSkillCount, InstalledSkillCount);
        SkillsSectionLabel = Strings.Format("Context_SkillsSectionTab", InstalledSkillCount);
    }

    private void RefreshMcpCounts()
    {
        ConfiguredMcpCount = McpServers.Count;
        ConnectedMcpCount = McpServers.Count(server => server.IsHealthy);
        McpCountLabel = Strings.Format("Context_McpCount", ConnectedMcpCount, ConfiguredMcpCount);
        McpSectionLabel = Strings.Format("Context_McpSectionTab", ConfiguredMcpCount);
    }

    private void RebuildFilteredSkills(bool preserveSelection)
    {
        var selectedName = preserveSelection ? SelectedSkill?.Name : null;
        FilteredSkills.Clear();

        IEnumerable<SkillSidebarItemViewModel> query = Skills;
        query = SkillFilterMode switch
        {
            SkillSidebarFilterMode.Enabled => query.Where(skill => skill.IsEnabled),
            SkillSidebarFilterMode.Disabled => query.Where(skill => !skill.IsEnabled),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SkillSearchText))
        {
            var term = SkillSearchText.Trim();
            query = query.Where(skill =>
                skill.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || skill.Description.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var skill in query)
        {
            FilteredSkills.Add(skill);
        }

        HasFilteredSkills = FilteredSkills.Count > 0;

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            var match = FilteredSkills.FirstOrDefault(skill =>
                string.Equals(skill.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            SelectedSkill = match;
            return;
        }

        if (SelectedSkill is not null
            && FilteredSkills.All(skill =>
                !string.Equals(skill.Name, SelectedSkill.Name, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedSkill = FilteredSkills.Count > 0 ? FilteredSkills[0] : null;
        }
        else if (SelectedSkill is null && FilteredSkills.Count > 0 && HasInstalledSkills)
        {
            SelectedSkill = FilteredSkills[0];
        }
    }

    private void RebuildFilteredMcpServers()
    {
        FilteredMcpServers.Clear();
        IEnumerable<McpSidebarServerViewModel> query = McpServers;
        if (!string.IsNullOrWhiteSpace(McpSearchText))
        {
            var term = McpSearchText.Trim();
            query = query.Where(server =>
                server.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || server.ToolNames.Any(tool =>
                    tool.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var server in query)
        {
            FilteredMcpServers.Add(server);
        }

        HasFilteredMcpServers = FilteredMcpServers.Count > 0;
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
