using System.Collections.ObjectModel;
using System.Windows;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Storage;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class TeamsPageViewModel : ObservableObject
{
    private static readonly string[] CatalogTools =
    [
        "file_list",
        "file_read",
        "file_write",
        "grep_files",
        "glob_files",
        "execute_command",
        "team_*",
        "sessions_*"
    ];

    private readonly AppSettings _settings;
    private readonly IAppSettingsRepository _settingsRepository;
    private readonly ITeamDefinitionStore _teamStore;
    private readonly IAgentProfileStore _profileStore;
    private readonly ITeamTaskStore _taskStore;
    private readonly ITeamTraceStore _traceStore;
    private readonly ITeamTaskOrchestrator _orchestrator;
    private readonly IActiveAgentSessionContext _activeSession;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IKnowledgeStore _knowledgeStore;
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, AgentProfile> _profilesById = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressCapabilitySync;

    public TeamsPageViewModel(
        AppSettings settings,
        IAppSettingsRepository settingsRepository,
        ITeamDefinitionStore teamStore,
        IAgentProfileStore profileStore,
        ITeamTaskStore taskStore,
        ITeamTraceStore traceStore,
        ITeamTaskOrchestrator orchestrator,
        IActiveAgentSessionContext activeSession,
        IAgentSkillCatalog skillCatalog,
        IKnowledgeStore knowledgeStore,
        IAppLogger logger)
    {
        _settings = settings;
        _settingsRepository = settingsRepository;
        _teamStore = teamStore;
        _profileStore = profileStore;
        _taskStore = taskStore;
        _traceStore = traceStore;
        _orchestrator = orchestrator;
        _activeSession = activeSession;
        _skillCatalog = skillCatalog;
        _knowledgeStore = knowledgeStore;
        _logger = logger.ForContext("TeamsPage");
        TeamsEnabled = settings.Teams.Enabled;
    }

    public ObservableCollection<TeamListItemViewModel> TeamItems { get; } = new();
    public ObservableCollection<TeamMemberItemViewModel> Members { get; } = new();
    public ObservableCollection<CapabilityOptionViewModel> ToolOptions { get; } = new();
    public ObservableCollection<CapabilityOptionViewModel> SkillOptions { get; } = new();
    public ObservableCollection<CapabilityOptionViewModel> McpOptions { get; } = new();
    public ObservableCollection<CapabilityOptionViewModel> KnowledgeOptions { get; } = new();
    public ObservableCollection<OrgPreviewNodeViewModel> PreviewWorkers { get; } = new();
    public ObservableCollection<TeamTask> RecentTasks { get; } = new();

    [ObservableProperty] private bool _teamsEnabled;
    [ObservableProperty] private TeamListItemViewModel? _selectedTeamItem;
    [ObservableProperty] private TeamMemberItemViewModel? _selectedMember;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _runGoal = "整理一次发版材料摘要";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _activeCapabilityTab = "tools";
    [ObservableProperty] private string _previewLeaderName = "Leader";

    public TeamDefinition? SelectedTeam => SelectedTeamItem?.Team;

    public AgentProfile? SelectedProfile => SelectedMember?.Profile;

    public bool HasSelectedTeam => SelectedTeamItem is not null;

    public bool IsLoopPolicyEnabled => SelectedMember is { IsLeader: false };

    public string EnabledBadgeText => TeamsEnabled ? "已启用" : "未启用";

    public bool IsToolsTab => ActiveCapabilityTab == "tools";
    public bool IsSkillsTab => ActiveCapabilityTab == "skills";
    public bool IsMcpTab => ActiveCapabilityTab == "mcp";
    public bool IsKnowledgeTab => ActiveCapabilityTab == "kb";

    public string EditingTeamName
    {
        get => SelectedTeam?.Name ?? "";
        set
        {
            if (SelectedTeam is null || SelectedTeam.Name == value)
            {
                return;
            }

            SelectedTeam.Name = value;
            SelectedTeamItem?.RefreshMeta();
            OnPropertyChanged();
        }
    }

    public string EditingTeamDescription
    {
        get => SelectedTeam?.Description ?? "";
        set
        {
            if (SelectedTeam is null || SelectedTeam.Description == value)
            {
                return;
            }

            SelectedTeam.Description = value;
            OnPropertyChanged();
        }
    }

    public int EditingMaxParallelWorkers
    {
        get => SelectedTeam?.MaxParallelWorkers ?? 1;
        set
        {
            if (SelectedTeam is null)
            {
                return;
            }

            var clamped = Math.Clamp(value, 1, 8);
            if (SelectedTeam.MaxParallelWorkers == clamped)
            {
                return;
            }

            SelectedTeam.MaxParallelWorkers = clamped;
            SelectedTeamItem?.RefreshMeta();
            OnPropertyChanged();
        }
    }

    public string EditingMemberName
    {
        get => SelectedProfile?.Name ?? "";
        set
        {
            if (SelectedProfile is null || SelectedProfile.Name == value)
            {
                return;
            }

            SelectedProfile.Name = value;
            SelectedMember?.RefreshFromProfile();
            RebuildOrgPreview();
            OnPropertyChanged();
        }
    }

    public string EditingMemberAgentMd
    {
        get => SelectedProfile?.AgentMd ?? "";
        set
        {
            if (SelectedProfile is null || SelectedProfile.AgentMd == value)
            {
                return;
            }

            SelectedProfile.AgentMd = value;
            OnPropertyChanged();
        }
    }

    public string EditingMemberDescription
    {
        get => SelectedProfile?.Description ?? "";
        set
        {
            if (SelectedProfile is null || SelectedProfile.Description == value)
            {
                return;
            }

            SelectedProfile.Description = value;
            SelectedMember?.RefreshFromProfile();
            OnPropertyChanged();
        }
    }

    public bool EditingAllowRedispatch
    {
        get => SelectedProfile?.AllowRedispatch ?? false;
        set
        {
            if (SelectedProfile is null || SelectedProfile.AllowRedispatch == value)
            {
                return;
            }

            SelectedProfile.AllowRedispatch = value;
            OnPropertyChanged();
        }
    }

    public int EditingMaxIterations
    {
        get => SelectedProfile?.MaxIterations ?? 1;
        set
        {
            if (SelectedProfile is null)
            {
                return;
            }

            var clamped = Math.Clamp(value, 1, 10);
            if (SelectedProfile.MaxIterations == clamped)
            {
                return;
            }

            SelectedProfile.MaxIterations = clamped;
            OnPropertyChanged();
        }
    }

    partial void OnTeamsEnabledChanged(bool value)
    {
        _settings.Teams.Enabled = value;
        OnPropertyChanged(nameof(EnabledBadgeText));
    }

    partial void OnSelectedTeamItemChanged(TeamListItemViewModel? value)
    {
        foreach (var item in TeamItems)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        OnPropertyChanged(nameof(SelectedTeam));
        OnPropertyChanged(nameof(HasSelectedTeam));
        NotifyTeamEditingProperties();
        RebuildMembers();
    }

    partial void OnSelectedMemberChanged(TeamMemberItemViewModel? value)
    {
        foreach (var member in Members)
        {
            member.IsSelected = ReferenceEquals(member, value);
        }

        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(IsLoopPolicyEnabled));
        NotifyMemberEditingProperties();
        SyncCapabilityOptionsFromProfile();
        RebuildOrgPreview();
    }

    private void NotifyTeamEditingProperties()
    {
        OnPropertyChanged(nameof(EditingTeamName));
        OnPropertyChanged(nameof(EditingTeamDescription));
        OnPropertyChanged(nameof(EditingMaxParallelWorkers));
    }

    private void NotifyMemberEditingProperties()
    {
        OnPropertyChanged(nameof(EditingMemberName));
        OnPropertyChanged(nameof(EditingMemberAgentMd));
        OnPropertyChanged(nameof(EditingMemberDescription));
        OnPropertyChanged(nameof(EditingAllowRedispatch));
        OnPropertyChanged(nameof(EditingMaxIterations));
    }

    partial void OnActiveCapabilityTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsToolsTab));
        OnPropertyChanged(nameof(IsSkillsTab));
        OnPropertyChanged(nameof(IsMcpTab));
        OnPropertyChanged(nameof(IsKnowledgeTab));
    }

    public async Task RefreshAsync()
    {
        var selectedTeamId = SelectedTeamItem?.Team.Id;
        var selectedProfileId = SelectedMember?.Profile.Id;

        TeamItems.Clear();
        Members.Clear();
        RecentTasks.Clear();
        _profilesById.Clear();

        foreach (var profile in await _profileStore.ListAsync().ConfigureAwait(true))
        {
            _profilesById[profile.Id] = profile;
        }

        foreach (var team in await _teamStore.ListAsync().ConfigureAwait(true))
        {
            TeamItems.Add(new TeamListItemViewModel(team));
        }

        SelectedTeamItem = TeamItems.FirstOrDefault(t => t.Team.Id == selectedTeamId)
            ?? TeamItems.FirstOrDefault();

        if (SelectedTeamItem is not null && !string.IsNullOrWhiteSpace(selectedProfileId))
        {
            SelectedMember = Members.FirstOrDefault(m => m.Profile.Id == selectedProfileId)
                ?? Members.FirstOrDefault();
        }

        await LoadCatalogOptionsAsync().ConfigureAwait(true);
        SyncCapabilityOptionsFromProfile();

        var sessionId = _activeSession.SessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            foreach (var task in await _taskStore.ListForRootSessionAsync(sessionId).ConfigureAwait(true))
            {
                RecentTasks.Add(task);
            }
        }

        StatusMessage = TeamsEnabled
            ? $"{TeamItems.Count} 个 Team · {_profilesById.Count} 个 Profile"
            : "Teams 未启用 — 打开开关并保存后可运行 Team Task。";
    }

    [RelayCommand]
    private void SelectCapabilityTab(string? tab)
    {
        if (string.IsNullOrWhiteSpace(tab))
        {
            return;
        }

        ActiveCapabilityTab = tab;
    }

    [RelayCommand]
    private void SelectMember(TeamMemberItemViewModel? member)
    {
        if (member is null)
        {
            return;
        }

        SelectedMember = member;
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        ApplyCapabilityOptionsToProfile();
        _settings.Teams.Enabled = TeamsEnabled;
        await _settingsRepository.SaveSettingsAsync(_settings).ConfigureAwait(true);

        if (SelectedTeam is not null)
        {
            SelectedTeam.UpdatedAt = DateTimeOffset.UtcNow;
            await _teamStore.SaveAsync(SelectedTeam).ConfigureAwait(true);
            SelectedTeamItem?.RefreshMeta();
        }

        if (SelectedProfile is not null)
        {
            await _profileStore.SaveAsync(SelectedProfile).ConfigureAwait(true);
            SelectedMember?.RefreshFromProfile();
        }

        StatusMessage = "已保存 Team 与成员配置。";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CreateTeamAsync()
    {
        var leader = new AgentProfile
        {
            Id = "profile-" + Guid.NewGuid().ToString("N")[..10],
            Name = "New Leader",
            Description = "统筹拆解 · 分派 · 汇总验收",
            AgentMd = "你是 Team Leader。理解目标，拆分给 Workers，汇总可验收结果。优先使用 team_* 工具。",
            AllowRedispatch = false,
            MaxIterations = 1,
            ToolAllowList = TeamToolDefaults.WorkerSafeTools.Concat(TeamToolDefaults.LeaderExtraTools).ToList()
        };
        await _profileStore.SaveAsync(leader).ConfigureAwait(true);

        var team = new TeamDefinition
        {
            Id = "team-" + Guid.NewGuid().ToString("N")[..10],
            Name = "New Team",
            Description = "新建的 Leader + Workers 团队。",
            LeaderProfileId = leader.Id,
            WorkerProfileIds = [],
            MaxParallelWorkers = 2
        };
        await _teamStore.SaveAsync(team).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SelectedTeamItem = TeamItems.FirstOrDefault(t => t.Team.Id == team.Id);
        StatusMessage = "已创建新 Team。";
    }

    [RelayCommand]
    private async Task AddWorkerAsync()
    {
        if (SelectedTeam is null)
        {
            StatusMessage = "请先选择或创建一个 Team。";
            return;
        }

        var worker = new AgentProfile
        {
            Id = "profile-" + Guid.NewGuid().ToString("N")[..10],
            Name = "New Worker",
            Description = "执行分派任务",
            AgentMd = "你只执行 Leader 分派的任务，输出结构化结果。",
            AllowRedispatch = true,
            MaxIterations = 2,
            ToolAllowList = TeamToolDefaults.WorkerSafeTools.ToList()
        };
        await _profileStore.SaveAsync(worker).ConfigureAwait(true);

        SelectedTeam.WorkerProfileIds.Add(worker.Id);
        SelectedTeam.UpdatedAt = DateTimeOffset.UtcNow;
        await _teamStore.SaveAsync(SelectedTeam).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SelectedMember = Members.FirstOrDefault(m => m.Profile.Id == worker.Id);
        StatusMessage = "已添加 Worker。";
    }

    [RelayCommand]
    private async Task CreateSampleTeamAsync()
    {
        var leader = new AgentProfile
        {
            Id = "profile-leader-release",
            Name = "Release Leader",
            Description = "统筹拆解 · 分派 · 汇总验收",
            AgentMd = "你是发版准备团队的 Leader。理解整体目标，拆分给 Engineering / QA / Docs Workers，汇总可验收的发布材料。不要自己做长篇撰写，优先分派。可对同一 Worker 循环重派（带 reason），达到 MaxIterations 必须收束。",
            AllowRedispatch = false,
            MaxIterations = 1,
            ToolAllowList = TeamToolDefaults.WorkerSafeTools.Concat(TeamToolDefaults.LeaderExtraTools).ToList(),
            SkillNames = ["release-notes", "risk-checklist"]
        };
        var eng = new AgentProfile
        {
            Id = "profile-worker-eng",
            Name = "Engineering Notes",
            Description = "整理变更说明与风险点",
            AgentMd = "你只负责从代码与 PR 中整理变更说明、兼容性风险。输出结构化要点，不要改写最终用户文案。",
            AllowRedispatch = false,
            MaxIterations = 1,
            ToolAllowList = ["file_read", "grep_files", "glob_files", "knowledge_search"],
            SkillNames = ["git-summary"],
            KnowledgeModuleIds = []
        };
        var qa = new AgentProfile
        {
            Id = "profile-worker-qa",
            Name = "QA Summary",
            Description = "汇总测试结论与阻断项",
            AgentMd = "你只汇总测试结论、阻断项与回归范围。引用用例与结果，不要编写产品宣传文案。可被 Leader 循环重派以补测。",
            AllowRedispatch = true,
            MaxIterations = 3,
            ToolAllowList = ["file_list", "file_read", "file_write", "execute_command", "grep_files", "glob_files"],
            SkillNames = ["risk-checklist"]
        };
        var docs = new AgentProfile
        {
            Id = "profile-worker-docs",
            Name = "Docs Writer",
            Description = "撰写用户可见发布说明",
            AgentMd = "你根据 Engineering/QA 产物撰写用户可见发布说明，语气清晰克制，不发明未确认事实。",
            AllowRedispatch = true,
            MaxIterations = 2,
            ToolAllowList = ["file_list", "file_read", "file_write", "file_edit"],
            SkillNames = ["release-notes", "git-summary"]
        };

        await _profileStore.SaveAsync(leader).ConfigureAwait(true);
        await _profileStore.SaveAsync(eng).ConfigureAwait(true);
        await _profileStore.SaveAsync(qa).ConfigureAwait(true);
        await _profileStore.SaveAsync(docs).ConfigureAwait(true);

        var team = new TeamDefinition
        {
            Id = "team-release-prep",
            Name = "Release Prep Squad",
            Description = "负责发版前的变更说明、测试结论整理与发布材料汇总。Leader 统筹；Workers 分领域执行。",
            LeaderProfileId = leader.Id,
            WorkerProfileIds = [eng.Id, qa.Id, docs.Id],
            MaxParallelWorkers = 2
        };
        await _teamStore.SaveAsync(team).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SelectedTeamItem = TeamItems.FirstOrDefault(t => t.Team.Id == team.Id);
        StatusMessage = "已创建示例 Team（Release Prep Squad）。";
    }

    [RelayCommand]
    private async Task RunSelectedTeamAsync()
    {
        if (!TeamsEnabled)
        {
            StatusMessage = "请先启用 Teams 并保存。";
            return;
        }

        if (SelectedTeam is null)
        {
            StatusMessage = "请选择 Team。";
            return;
        }

        var sessionId = _activeSession.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            StatusMessage = "请先打开一个 Chat 会话（需要 RootSessionId）。";
            return;
        }

        if (string.IsNullOrWhiteSpace(RunGoal))
        {
            StatusMessage = "请输入运行目标。";
            return;
        }

        IsBusy = true;
        StatusMessage = "正在运行 Team Task…";
        try
        {
            var result = await _orchestrator.StartAsync(
                new TeamTaskStartRequest
                {
                    TeamId = SelectedTeam.Id,
                    RootSessionId = sessionId,
                    Goal = RunGoal.Trim()
                }).ConfigureAwait(true);

            StatusMessage = result.Ok
                ? $"Task {result.Task.TaskId}: {result.Task.Status}"
                : result.Error ?? "Failed";

            await RefreshAsync().ConfigureAwait(true);
            if (result.Ok)
            {
                await OpenTraceAsync(result.Task).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Team run failed");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenSelectedTraceAsync() => OpenLatestTraceAsync();

    public async Task OpenLatestTraceAsync()
    {
        if (RecentTasks.Count == 0)
        {
            var sessionId = _activeSession.SessionId;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                RecentTasks.Clear();
                foreach (var task in await _taskStore.ListForRootSessionAsync(sessionId).ConfigureAwait(true))
                {
                    RecentTasks.Add(task);
                }
            }
        }

        var latest = RecentTasks.FirstOrDefault();
        if (latest is null)
        {
            StatusMessage = "当前会话没有最近的 Team Task。";
            return;
        }

        await OpenTraceAsync(latest).ConfigureAwait(true);
    }

    public async Task OpenTraceAsync(TeamTask task)
    {
        var events = await _traceStore.ReadAsync(task.RootSessionId, task.TaskId).ConfigureAwait(true);
        var owner = Application.Current?.MainWindow;
        var teamName = TeamItems.FirstOrDefault(t => t.Team.Id == task.TeamId)?.Name
            ?? SelectedTeamItem?.Name
            ?? task.TeamId;
        if (string.IsNullOrWhiteSpace(teamName) || teamName == task.TeamId)
        {
            var team = await _teamStore.GetAsync(task.TeamId).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(team?.Name))
            {
                teamName = team.Name;
            }
        }

        TeamTraceWindow.ShowOrFocus(task, events, owner, _traceStore, teamName);
    }

    private void RebuildMembers()
    {
        Members.Clear();
        SelectedMember = null;
        if (SelectedTeam is null)
        {
            RebuildOrgPreview();
            return;
        }

        if (!string.IsNullOrWhiteSpace(SelectedTeam.LeaderProfileId) &&
            _profilesById.TryGetValue(SelectedTeam.LeaderProfileId, out var leader))
        {
            Members.Add(new TeamMemberItemViewModel(leader, isLeader: true));
        }

        foreach (var workerId in SelectedTeam.WorkerProfileIds)
        {
            if (_profilesById.TryGetValue(workerId, out var worker))
            {
                Members.Add(new TeamMemberItemViewModel(worker, isLeader: false));
            }
        }

        SelectedMember = Members.FirstOrDefault();
        RebuildOrgPreview();
    }

    private void RebuildOrgPreview()
    {
        PreviewWorkers.Clear();
        var leader = Members.FirstOrDefault(m => m.IsLeader);
        PreviewLeaderName = leader?.DisplayName ?? "Leader";
        foreach (var worker in Members.Where(m => !m.IsLeader))
        {
            PreviewWorkers.Add(new OrgPreviewNodeViewModel(worker.DisplayName, isLeader: false));
        }
    }

    private async Task LoadCatalogOptionsAsync()
    {
        ClearCapabilityHandlers();
        ToolOptions.Clear();
        SkillOptions.Clear();
        McpOptions.Clear();
        KnowledgeOptions.Clear();

        foreach (var tool in CatalogTools)
        {
            var enabled = !string.Equals(tool, "sessions_*", StringComparison.Ordinal);
            ToolOptions.Add(new CapabilityOptionViewModel(tool, tool == "team_*" ? "team_*（Leader）" : tool == "sessions_*" ? "sessions_*（禁用）" : tool, isChecked: false, isEnabled: enabled));
        }

        foreach (var skill in _skillCatalog.Skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            SkillOptions.Add(new CapabilityOptionViewModel(skill.Name, skill.Name, false));
        }

        // Keep sample skill names visible even if catalog empty.
        foreach (var fallback in new[] { "release-notes", "risk-checklist", "code-review", "git-summary" })
        {
            if (SkillOptions.All(s => !string.Equals(s.Id, fallback, StringComparison.OrdinalIgnoreCase)))
            {
                SkillOptions.Add(new CapabilityOptionViewModel(fallback, fallback, false));
            }
        }

        foreach (var server in _settings.McpServers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(server.Name))
            {
                McpOptions.Add(new CapabilityOptionViewModel(server.Name, server.Name, false));
            }
        }

        foreach (var fallback in new[] { "github", "jira", "slack", "browserbase" })
        {
            if (McpOptions.All(s => !string.Equals(s.Id, fallback, StringComparison.OrdinalIgnoreCase)))
            {
                McpOptions.Add(new CapabilityOptionViewModel(fallback, fallback, false));
            }
        }

        try
        {
            foreach (var module in await _knowledgeStore.ListModulesAsync().ConfigureAwait(true))
            {
                KnowledgeOptions.Add(new CapabilityOptionViewModel(module.Module.Id, module.Module.Name, false));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to list knowledge modules for Teams page: {Message}", ex.Message);
        }

        foreach (var fallback in new[] { "product-handbook", "eng-runbooks", "qa-playbooks", "release-archive" })
        {
            if (KnowledgeOptions.All(s => !string.Equals(s.Id, fallback, StringComparison.OrdinalIgnoreCase) &&
                                          !string.Equals(s.Label, fallback, StringComparison.OrdinalIgnoreCase)))
            {
                KnowledgeOptions.Add(new CapabilityOptionViewModel(fallback, fallback, false));
            }
        }

        WireCapabilityHandlers();
    }

    private void SyncCapabilityOptionsFromProfile()
    {
        _suppressCapabilitySync = true;
        try
        {
            var profile = SelectedProfile;
            var isLeader = SelectedMember?.IsLeader == true;
            var allow = profile is null
                ? Array.Empty<string>()
                : TeamToolDefaults.ResolveAllowList(profile, isLeader).ToArray();

            foreach (var option in ToolOptions)
            {
                if (!option.IsEnabled)
                {
                    option.IsChecked = false;
                    continue;
                }

                if (option.Id.EndsWith('*'))
                {
                    var prefix = option.Id[..^1];
                    option.IsChecked = allow.Any(t =>
                        t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t, option.Id, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    option.IsChecked = TeamToolDefaults.MatchesAllowList(option.Id, allow);
                }
            }

            foreach (var option in SkillOptions)
            {
                option.IsChecked = profile?.SkillNames.Contains(option.Id, StringComparer.OrdinalIgnoreCase) == true;
            }

            foreach (var option in McpOptions)
            {
                option.IsChecked = profile?.McpServerNames.Contains(option.Id, StringComparer.OrdinalIgnoreCase) == true;
            }

            foreach (var option in KnowledgeOptions)
            {
                option.IsChecked = profile?.KnowledgeModuleIds.Contains(option.Id, StringComparer.OrdinalIgnoreCase) == true;
            }
        }
        finally
        {
            _suppressCapabilitySync = false;
        }
    }

    private void ApplyCapabilityOptionsToProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.ToolAllowList = ToolOptions
            .Where(o => o.IsEnabled && o.IsChecked)
            .Select(o => o.Id)
            .ToList();
        SelectedProfile.SkillNames = SkillOptions.Where(o => o.IsChecked).Select(o => o.Id).ToList();
        SelectedProfile.McpServerNames = McpOptions.Where(o => o.IsChecked).Select(o => o.Id).ToList();
        SelectedProfile.KnowledgeModuleIds = KnowledgeOptions.Where(o => o.IsChecked).Select(o => o.Id).ToList();
        SelectedMember?.RefreshCaps(SelectedMember.IsLeader);
    }

    private void WireCapabilityHandlers()
    {
        foreach (var option in ToolOptions.Concat(SkillOptions).Concat(McpOptions).Concat(KnowledgeOptions))
        {
            option.PropertyChanged += OnCapabilityOptionChanged;
        }
    }

    private void ClearCapabilityHandlers()
    {
        foreach (var option in ToolOptions.Concat(SkillOptions).Concat(McpOptions).Concat(KnowledgeOptions))
        {
            option.PropertyChanged -= OnCapabilityOptionChanged;
        }
    }

    private void OnCapabilityOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressCapabilitySync || e.PropertyName != nameof(CapabilityOptionViewModel.IsChecked))
        {
            return;
        }

        ApplyCapabilityOptionsToProfile();
    }
}
