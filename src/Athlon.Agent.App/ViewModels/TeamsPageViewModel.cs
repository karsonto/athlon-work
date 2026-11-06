using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
    private readonly Dictionary<string, CancellationTokenSource> _runCtsBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string RootSessionId, string TaskId)> _pendingAsksBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressCapabilitySync;
    private bool _catalogLoaded;

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
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _activeCapabilityTab = "tools";
    [ObservableProperty] private string _previewLeaderName = "Leader";

    public TeamDefinition? SelectedTeam => SelectedTeamItem?.Team;

    public AgentProfile? SelectedProfile => SelectedMember?.Profile;

    public bool HasSelectedTeam => SelectedTeamItem is not null;

    public bool CanDeleteSelectedWorker => SelectedMember is { IsLeader: false };

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
        OnPropertyChanged(nameof(CanDeleteSelectedWorker));
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

    /// <summary>
    /// First open loads from disk; later visits keep in-memory SPA edits (Chat Teams runs must
    /// not reshuffle or append Team definitions on this page).
    /// </summary>
    public Task EnsureLoadedAsync() =>
        _catalogLoaded ? Task.CompletedTask : RefreshAsync();

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
        await RefreshRecentTasksAsync().ConfigureAwait(true);
        _catalogLoaded = true;

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

        // Persist every member currently on the page so SPA-style edits survive without a reload.
        foreach (var member in Members.ToList())
        {
            await _profileStore.SaveAsync(member.Profile).ConfigureAwait(true);
            member.RefreshFromProfile();
        }

        StatusMessage = "已保存 Team 与成员配置。";
    }

    [RelayCommand]
    private async Task CreateTeamAsync()
    {
        ApplyCapabilityOptionsToProfile();

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

        _profilesById[leader.Id] = leader;
        var item = new TeamListItemViewModel(team);
        TeamItems.Add(item);
        SelectedTeamItem = item;
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

        // Keep in-memory edits for the currently open member/team; only mutate the member strip.
        ApplyCapabilityOptionsToProfile();

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

        _profilesById[worker.Id] = worker;
        var item = new TeamMemberItemViewModel(worker, isLeader: false);
        Members.Add(item);
        SelectedTeamItem?.RefreshMeta();
        RebuildOrgPreview();
        SelectedMember = item;
        StatusMessage = "已添加 Worker。";
    }

    [RelayCommand]
    private async Task DeleteTeamAsync()
    {
        if (SelectedTeam is null || SelectedTeamItem is null)
        {
            StatusMessage = "请先选择要删除的 Team。";
            return;
        }

        var team = SelectedTeam;
        var name = string.IsNullOrWhiteSpace(team.Name) ? team.Id : team.Name;
        var confirm = MessageBox.Show(
            $"确定删除 Team「{name}」？\n将同时删除未被其他 Team 引用的 Leader / Worker Profile。",
            "删除 Team",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(team.LeaderProfileId))
        {
            profileIds.Add(team.LeaderProfileId);
        }

        foreach (var workerId in team.WorkerProfileIds)
        {
            if (!string.IsNullOrWhiteSpace(workerId))
            {
                profileIds.Add(workerId);
            }
        }

        await _teamStore.DeleteAsync(team.Id).ConfigureAwait(true);

        var remainingTeams = TeamItems
            .Where(t => !string.Equals(t.Team.Id, team.Id, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Team)
            .ToList();
        foreach (var profileId in profileIds)
        {
            if (!IsProfileReferenced(profileId, remainingTeams))
            {
                await _profileStore.DeleteAsync(profileId).ConfigureAwait(true);
                _profilesById.Remove(profileId);
            }
        }

        var removeIndex = TeamItems.IndexOf(SelectedTeamItem);
        TeamItems.Remove(SelectedTeamItem);
        SelectedTeamItem = TeamItems.ElementAtOrDefault(Math.Max(0, removeIndex - 1))
            ?? TeamItems.FirstOrDefault();
        StatusMessage = $"已删除 Team「{name}」。";
    }

    [RelayCommand]
    private async Task DeleteWorkerAsync(TeamMemberItemViewModel? member)
    {
        member ??= SelectedMember;
        if (SelectedTeam is null || member is null)
        {
            StatusMessage = "请先选择要删除的 Worker。";
            return;
        }

        if (member.IsLeader)
        {
            StatusMessage = "不能删除 Leader；请删除整个 Team，或先更换 Leader。";
            return;
        }

        var profileId = member.Profile.Id;
        var name = member.DisplayName;
        var confirm = MessageBox.Show(
            $"确定从当前 Team 移除 Worker「{name}」？\n若该 Profile 未被其他 Team 引用，将一并删除。",
            "删除 Worker",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        ApplyCapabilityOptionsToProfile();

        SelectedTeam.WorkerProfileIds.RemoveAll(id =>
            string.Equals(id, profileId, StringComparison.OrdinalIgnoreCase));
        SelectedTeam.UpdatedAt = DateTimeOffset.UtcNow;
        await _teamStore.SaveAsync(SelectedTeam).ConfigureAwait(true);

        var teams = TeamItems.Select(t => t.Team).ToList();
        if (!IsProfileReferenced(profileId, teams))
        {
            await _profileStore.DeleteAsync(profileId).ConfigureAwait(true);
            _profilesById.Remove(profileId);
        }

        var removingSelected = ReferenceEquals(SelectedMember, member)
            || string.Equals(SelectedMember?.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
        Members.Remove(member);
        SelectedTeamItem?.RefreshMeta();
        RebuildOrgPreview();
        if (removingSelected)
        {
            SelectedMember = Members.FirstOrDefault(m => !m.IsLeader) ?? Members.FirstOrDefault();
        }

        StatusMessage = $"已删除 Worker「{name}」。";
    }

    private static bool IsProfileReferenced(string profileId, IReadOnlyList<TeamDefinition> teams)
    {
        foreach (var team in teams)
        {
            if (string.Equals(team.LeaderProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (team.WorkerProfileIds.Any(id => string.Equals(id, profileId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
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

        _profilesById[leader.Id] = leader;
        _profilesById[eng.Id] = eng;
        _profilesById[qa.Id] = qa;
        _profilesById[docs.Id] = docs;

        var existing = TeamItems.FirstOrDefault(t =>
            string.Equals(t.Team.Id, team.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var index = TeamItems.IndexOf(existing);
            TeamItems[index] = new TeamListItemViewModel(team);
            SelectedTeamItem = TeamItems[index];
        }
        else
        {
            var item = new TeamListItemViewModel(team);
            TeamItems.Add(item);
            SelectedTeamItem = item;
        }

        StatusMessage = "已创建示例 Team（Release Prep Squad）。";
    }

    /// <summary>
    /// Cancels the in-flight Team run for <paramref name="rootSessionId"/> (or all sessions when null).
    /// Returns true when at least one run was cancelled.
    /// </summary>
    public bool CancelActiveTeamRun(string? rootSessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(rootSessionId))
        {
            return CancelRunForSession(rootSessionId);
        }

        var any = false;
        foreach (var sessionId in _runCtsBySession.Keys.ToList())
        {
            any |= CancelRunForSession(sessionId);
        }

        return any;
    }

    public bool IsTeamRunActive(string? rootSessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(rootSessionId))
        {
            return _runCtsBySession.TryGetValue(rootSessionId, out var cts)
                   && !cts.IsCancellationRequested;
        }

        return IsBusy || _runCtsBySession.Count > 0;
    }

    public bool TryGetPendingAsk(string rootSessionId, out string taskId)
    {
        if (_pendingAsksBySession.TryGetValue(rootSessionId, out var pending))
        {
            taskId = pending.TaskId;
            return true;
        }

        taskId = "";
        return false;
    }

    /// <summary>Resumes a Team Task paused on <see cref="TeamTaskStatus.AwaitingHuman"/>.</summary>
    public async Task<(bool Ok, string Message, TeamTask? Task)> ResumeTeamAskAsync(
        string rootSessionId,
        string humanAnswer,
        bool openTrace = true,
        CancellationToken cancellationToken = default)
    {
        if (!_pendingAsksBySession.TryGetValue(rootSessionId, out var pending))
        {
            return (false, "No pending team question", null);
        }

        if (string.IsNullOrWhiteSpace(humanAnswer))
        {
            return (false, "Empty answer", null);
        }

        CancelRunForSession(rootSessionId);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCtsBySession[rootSessionId] = runCts;
        IsBusy = true;
        try
        {
            if (openTrace)
            {
                var existing = await _taskStore.GetAsync(pending.RootSessionId, pending.TaskId, runCts.Token)
                    .ConfigureAwait(true);
                if (existing is not null)
                {
                    await OpenTraceAsync(existing, live: true).ConfigureAwait(true);
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
            }

            var completed = await _orchestrator
                .RunToCompletionAsync(
                    pending.RootSessionId,
                    pending.TaskId,
                    runCts.Token,
                    humanAnswer.Trim())
                .ConfigureAwait(true);

            return FinalizeTeamRunResult(rootSessionId, completed);
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled", null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Team ask resume failed");
            return (false, ex.Message, null);
        }
        finally
        {
            ClearRunCts(rootSessionId, runCts);
            IsBusy = false;
        }
    }

    /// <summary>
    /// Runs a Team Task for Chat / schedule. Does not mutate Teams config page state
    /// (no auto-enable, no Team list refresh, no synthetic Team creation).
    /// </summary>
    public async Task<(bool Ok, string Message, TeamTask? Task)> RunTeamGoalAsync(
        string rootSessionId,
        string goal,
        string? teamId = null,
        bool openTrace = true,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Teams.Enabled)
        {
            return (false, "Teams disabled", null);
        }

        teamId ??= SelectedTeam?.Id ?? _settings.Teams.SelectedTeamId;
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return (false, "No team selected", null);
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            return (false, "Empty goal", null);
        }

        if (string.IsNullOrWhiteSpace(rootSessionId))
        {
            return (false, "Missing root session", null);
        }

        CancelRunForSession(rootSessionId);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCtsBySession[rootSessionId] = runCts;
        IsBusy = true;
        try
        {
            var request = new TeamTaskStartRequest
            {
                TeamId = teamId,
                RootSessionId = rootSessionId,
                Goal = goal.Trim()
            };

            // Open Trace immediately so the user can watch Leader/Workers live.
            var began = await _orchestrator.BeginAsync(request, runCts.Token).ConfigureAwait(true);
            if (!began.Ok)
            {
                return (false, began.Error ?? "Failed", began.Task);
            }

            if (openTrace)
            {
                await OpenTraceAsync(began.Task, live: true).ConfigureAwait(true);
                // Let the Trace window paint before the Leader turn blocks the UI thread.
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            var completed = await _orchestrator
                .RunToCompletionAsync(began.Task.RootSessionId, began.Task.TaskId, runCts.Token)
                .ConfigureAwait(true);

            return FinalizeTeamRunResult(rootSessionId, completed);
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled", null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Team run failed");
            return (false, ex.Message, null);
        }
        finally
        {
            ClearRunCts(rootSessionId, runCts);
            IsBusy = false;
        }
    }

    private (bool Ok, string Message, TeamTask? Task) FinalizeTeamRunResult(string rootSessionId, TeamTask completed)
    {
        if (completed.Status == TeamTaskStatus.AwaitingHuman)
        {
            _pendingAsksBySession[rootSessionId] = (completed.RootSessionId, completed.TaskId);
            var question = completed.PendingHumanQuestion ?? "Team needs your input.";
            return (false, question, completed);
        }

        _pendingAsksBySession.Remove(rootSessionId);

        var ok = completed.Status == TeamTaskStatus.Succeeded
                 && !string.IsNullOrWhiteSpace(completed.FinalResult);
        var message = ok
            ? completed.FinalResult!
            : completed.Error
              ?? (completed.Status == TeamTaskStatus.Succeeded
                  ? "Succeeded without FinalResult"
                  : completed.Status.ToString());
        return (ok, message, completed);
    }

    private bool CancelRunForSession(string rootSessionId)
    {
        if (!_runCtsBySession.TryGetValue(rootSessionId, out var cts)
            || cts.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ClearRunCts(string rootSessionId, CancellationTokenSource runCts)
    {
        if (_runCtsBySession.TryGetValue(rootSessionId, out var current)
            && ReferenceEquals(current, runCts))
        {
            _runCtsBySession.Remove(rootSessionId);
        }

        runCts.Dispose();
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

        var live = latest.Status is TeamTaskStatus.Running or TeamTaskStatus.AwaitingHuman;
        await OpenTraceAsync(latest, live).ConfigureAwait(true);
    }

    public async Task OpenTraceAsync(TeamTask task, bool live = false)
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

        var shouldLive = live
            || task.Status is TeamTaskStatus.Running or TeamTaskStatus.AwaitingHuman;

        TeamTraceWindow.ShowOrFocus(
            task,
            events,
            owner,
            _traceStore,
            teamName,
            _taskStore,
            shouldLive);
    }

    public async Task OpenTraceByTaskIdAsync(string rootSessionId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(rootSessionId) || string.IsNullOrWhiteSpace(taskId))
        {
            StatusMessage = "无效的 Trace 链接。";
            return;
        }

        var task = await _taskStore.GetAsync(rootSessionId, taskId).ConfigureAwait(true);
        if (task is null)
        {
            // Fall back to latest task in this session when the deep-link target is gone.
            var listed = await _taskStore.ListForRootSessionAsync(rootSessionId).ConfigureAwait(true);
            task = listed.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                ?? listed.FirstOrDefault();
            if (task is null)
            {
                StatusMessage = $"找不到 Team Task：{taskId}";
                return;
            }
        }

        var live = task.Status is TeamTaskStatus.Running or TeamTaskStatus.AwaitingHuman;
        await OpenTraceAsync(task, live).ConfigureAwait(true);
    }

    private void RebuildMembers()
    {
        var previousProfileId = SelectedMember?.Profile.Id;
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

        SelectedMember = Members.FirstOrDefault(m =>
                             string.Equals(m.Profile.Id, previousProfileId, StringComparison.OrdinalIgnoreCase))
                         ?? Members.FirstOrDefault();
        RebuildOrgPreview();
    }

    private async Task RefreshRecentTasksAsync()
    {
        RecentTasks.Clear();
        var sessionId = _activeSession.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        foreach (var task in await _taskStore.ListForRootSessionAsync(sessionId).ConfigureAwait(true))
        {
            RecentTasks.Add(task);
        }
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
