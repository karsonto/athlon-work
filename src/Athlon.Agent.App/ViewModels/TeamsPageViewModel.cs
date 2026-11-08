using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.App.Services;
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
    /// <summary>
    /// Worker-selectable execution tools only.
    /// team_progress / team_memory always attached;
    /// load_skill_through_path / knowledge_search auto-attached when Skills / Knowledge selected.
    /// Leader orchestration tools are never selectable for Workers.
    /// </summary>
    private static readonly string[] CatalogTools =
    [
        "file_list",
        "file_read",
        "file_write",
        "file_edit",
        "apply_patch",
        "grep_files",
        "glob_files",
        "execute_command"
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
        TeamChatArtifacts.ConfigureTaskStore(taskStore);
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

    /// <summary>Workers may pick tools/skills/MCP/knowledge; Leaders use fixed team orchestration tools only.</summary>
    public bool IsWorkerCapabilitiesEditable => SelectedMember is { IsLeader: false };

    public bool IsLeaderCapabilitiesLocked => SelectedMember is { IsLeader: true };

    public string EnabledBadgeText => TeamsEnabled ? "已启用" : "未启用";

    public bool IsToolsTab => ActiveCapabilityTab == "tools";
    public bool IsSkillsTab => ActiveCapabilityTab == "skills";
    public bool IsMcpTab => ActiveCapabilityTab == "mcp";
    public bool IsKnowledgeTab => ActiveCapabilityTab == "kb";

    public bool ShowWorkerToolsTab => IsWorkerCapabilitiesEditable && IsToolsTab;
    public bool ShowWorkerSkillsTab => IsWorkerCapabilitiesEditable && IsSkillsTab;
    public bool ShowWorkerMcpTab => IsWorkerCapabilitiesEditable && IsMcpTab;
    public bool ShowWorkerKnowledgeTab => IsWorkerCapabilitiesEditable && IsKnowledgeTab;

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
        OnPropertyChanged(nameof(IsWorkerCapabilitiesEditable));
        OnPropertyChanged(nameof(IsLeaderCapabilitiesLocked));
        NotifyCapabilityTabVisibility();
        foreach (var member in Members)
        {
            member.IsSelected = ReferenceEquals(member, value);
        }

        OnPropertyChanged(nameof(SelectedProfile));
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
    }

    private void NotifyCapabilityTabVisibility()
    {
        OnPropertyChanged(nameof(IsToolsTab));
        OnPropertyChanged(nameof(IsSkillsTab));
        OnPropertyChanged(nameof(IsMcpTab));
        OnPropertyChanged(nameof(IsKnowledgeTab));
        OnPropertyChanged(nameof(ShowWorkerToolsTab));
        OnPropertyChanged(nameof(ShowWorkerSkillsTab));
        OnPropertyChanged(nameof(ShowWorkerMcpTab));
        OnPropertyChanged(nameof(ShowWorkerKnowledgeTab));
    }

    partial void OnActiveCapabilityTabChanged(string value) => NotifyCapabilityTabVisibility();

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
            ToolAllowList = TeamToolDefaults.DefaultLeaderAllowList.ToList()
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
            AgentMd = "你只执行 Leader 分派的任务，输出结构化 ## Result。可用 team_progress / team_memory 查看共享进度与记忆。",
            ToolAllowList = TeamToolDefaults.DefaultWorkerAllowList.ToList()
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
        var readTools = new[] { "file_list", "file_read", "grep_files", "glob_files" };
        var writeTools = new[]
        {
            "file_list", "file_read", "file_write", "file_edit", "apply_patch",
            "grep_files", "glob_files", "execute_command"
        };

        var leader = new AgentProfile
        {
            Id = "profile-leader-arch-optimizer",
            Name = "Architecture Optimizer Leader",
            Description = "编排配方批次 · 验收行为等价重构",
            AgentMd =
                "你是代码优化团队的 Leader。目标：用**可命名、可复查、行为等价、最小侵入**的「配方(recipe)」批次优化工作区代码"
                + "（清晰分层、类单一职责、消灭上帝类、降低耦合），而不是大段重写或风格大扫除。\n"
                + "默认流水线（depends_on 串行；禁止多人同时改同一批文件）：\n"
                + "1) Recipe Scout → dispatch\n"
                + "2) Recipe Author（depends_on Scout）→ dispatch\n"
                + "3) Recipe Runner（depends_on Author）→ dispatch\n"
                + "4) Recipe Verifier（depends_on Runner）→ dispatch\n"
                + "范围：用户指定路径/模块则只做该范围；否则每轮只批准 **1～3 条**高价值配方（优先 ExtractClass / ExtractMethod / MoveMethod / IntroduceInterface / BreakCycle）。\n"
                + "硬约束：保公开行为；禁止无关格式化、整仓搬家、无配方的「顺手清理」。Verifier fail → redispatch Runner（或补 Author）后再验。\n"
                + "全部通过后 team_collect / team_memory，按「应用了哪些配方 / 改了哪些符号与文件 / 剩余机会」汇总并 team_submit_result。你只编排，不读实现、不改文件。",
            ToolAllowList = TeamToolDefaults.DefaultLeaderAllowList.ToList(),
            SkillNames = []
        };

        var scout = new AgentProfile
        {
            Id = "profile-worker-arch-scout",
            Name = "Recipe Scout",
            Description = "搜索可机械落地的优化机会（配方候选）",
            AgentMd =
                "你负责「搜索」阶段：只发现机会，不改文件、不写执行步骤。用 file_list / glob / grep / file_read 做符号与调用关系摸底（类型、方法、引用、项目分层）。\n"
                + "只报告能映射到标准配方族的机会（每条必须可被 Runner 机械执行）：\n"
                + "- ExtractClass：上帝类/混杂职责（UI+IO+领域同文件），给出可拆出的方法/字段簇\n"
                + "- ExtractMethod：过长/过复杂方法的可抽连续语句块\n"
                + "- MoveMethod：Feature Envy（方法更依赖另一类型）\n"
                + "- IntroduceInterface / InvertDependency：跨层或枢纽耦合\n"
                + "- BreakCycle：import/项目引用环上的最小可断边\n"
                + "- SplitFile：单文件多顶层类型或模块社区可拆\n"
                + "- RemoveDeadCode：确认无引用的私有成员（不确定则不要报）\n"
                + "每条机会写清：id、recipe、target（path+symbol）、evidence（行数/方法数/依赖方）、severity(P0|P1|P2)、why_safe（为何可行为等价）。跳过纯审美/命名争论。\n"
                + "输出 ## Result：status、opportunities（≤12，按 severity）、scope_map。put team_memory 键 opportunity_catalog。",
            ToolAllowList = TeamToolDefaults.EnsureWorkerImpliedTools(readTools, skillNames: null).ToList(),
            SkillNames = [],
            KnowledgeModuleIds = []
        };

        var planner = new AgentProfile
        {
            Id = "profile-worker-arch-planner",
            Name = "Recipe Author",
            Description = "把机会写成可执行、可回滚的配方批次",
            AgentMd =
                "你负责编写配方：把 opportunity_catalog 编成本轮可执行 recipe batch，不改业务代码。先 team_memory 读目录，必要时精读目标文件确认符号边界。\n"
                + "只挑选本轮 1～3 条（优先 P0/P1、调用面可控、能编译）。每条配方必须包含：\n"
                + "- recipe_id / recipe（同上标准名）/ display_name\n"
                + "- preconditions（文件存在、符号仍在、依赖方向假设）\n"
                + "- search（精确符号/调用点/旧 import，禁止模糊全文替换）\n"
                + "- transform（有序原子步骤：新建类型→移动成员→改调用点→修 using/import→删除空壳）\n"
                + "- invariants（行为等价；保留原访问级别除非配方要求；保留注释与既有格式）\n"
                + "- affected_paths / rollback（git 级或逐步反向）\n"
                + "- acceptance（可检查断言，如「Foo 不再包含 Bar 逻辑」「A 不再引用 B」）\n"
                + "配方之间声明 order 与冲突（改同一文件则串行）。禁止「整体重写某文件」「顺便全项目改名」。\n"
                + "输出 ## Result：status、batch。put team_memory 键 recipe_batch。",
            ToolAllowList = TeamToolDefaults.EnsureWorkerImpliedTools(readTools, skillNames: null).ToList(),
            SkillNames = [],
            KnowledgeModuleIds = []
        };

        var engineer = new AgentProfile
        {
            Id = "profile-worker-arch-engineer",
            Name = "Recipe Runner",
            Description = "按配方最小侵入应用变换并逐步编译",
            AgentMd =
                "你负责按配方执行变换：严格按 recipe_batch 应用，不做计划外优化。先 team_memory 读 batch。\n"
                + "执行纪律：\n"
                + "1) 按 order 逐条配方；每条内按 transform 逐步做。\n"
                + "2) 大文件/多点改动用 apply_patch；小替换用 file_edit；新建文件可用 file_write。改前先 file_read 确认 search 仍命中。\n"
                + "3) 最小侵入：只改配方列出的符号与调用点；保留格式、注释、using 顺序（除非配方要求）。\n"
                + "4) 行为等价：不改算法语义；不删测试；不「为了绿而删断言」。\n"
                + "5) 每完成一条配方：execute_command 跑最窄构建（单项目/解决方案）；失败则就地修到该配方 precondition/acceptance 可过，再下一条。\n"
                + "6) 某条无法安全应用：跳过并记录 skipped_reason，不要用大重写硬上。\n"
                + "7) 全部做完后，最后一条助手消息必须是独立的 ## Result（含单独一行 status: completed|failed|…），"
                + "不要停在「编译绿了 / 下一步…」这类进度句；同时 put team_memory 键 recipe_results（applied/skipped/changed_files/build_commands）。",
            ToolAllowList = TeamToolDefaults.EnsureWorkerImpliedTools(writeTools, skillNames: null).ToList(),
            SkillNames = [],
            KnowledgeModuleIds = []
        };

        var verifier = new AgentProfile
        {
            Id = "profile-worker-arch-verifier",
            Name = "Recipe Verifier",
            Description = "验证配方 acceptance、构建测试与无越权改动",
            AgentMd =
                "你负责校验：证明本轮配方批次安全且达标。默认不改代码（仅允许修明显笔误级编译错误，并记入 notes）。\n"
                + "读 team_memory：recipe_batch、recipe_results。对每条 applied 配方：\n"
                + "- 核对 acceptance 是否成立（grep/read 符号与依赖方向）\n"
                + "- 核对 diff 范围是否落在 affected_paths（越权改动 → fail）\n"
                + "- 核对是否引入新的循环依赖或跨层泄漏\n"
                + "用 execute_command 跑构建；有测试则跑相关测试。失败：status:failed、verdict:fail，附命令与关键日志。\n"
                + "全部通过：verdict:pass，列出 residual_opportunities（未做的高价值机会，供下轮）。\n"
                + "最后一条助手消息必须是 ## Result（单独一行 status: + verdict:），不要停在进度句。"
                + "put team_memory 键 verify_report（per_recipe_checks/build/tests/residual_opportunities）。",
            ToolAllowList = TeamToolDefaults.EnsureWorkerImpliedTools(
                ["file_list", "file_read", "grep_files", "glob_files", "execute_command"],
                skillNames: null).ToList(),
            SkillNames = [],
            KnowledgeModuleIds = []
        };

        await _profileStore.SaveAsync(leader).ConfigureAwait(true);
        await _profileStore.SaveAsync(scout).ConfigureAwait(true);
        await _profileStore.SaveAsync(planner).ConfigureAwait(true);
        await _profileStore.SaveAsync(engineer).ConfigureAwait(true);
        await _profileStore.SaveAsync(verifier).ConfigureAwait(true);

        var team = new TeamDefinition
        {
            Id = "team-arch-optimizer",
            Name = "Architecture Optimizer",
            Description =
                "代码架构优化：搜索可机械落地的配方机会 → 编写小步行为等价配方 → 最小侵入执行 → 构建/验收；面向拆上帝类、单一职责与降耦合。",
            LeaderProfileId = leader.Id,
            WorkerProfileIds = [scout.Id, planner.Id, engineer.Id, verifier.Id],
            MaxParallelWorkers = 2
        };
        await _teamStore.SaveAsync(team).ConfigureAwait(true);

        _profilesById[leader.Id] = leader;
        _profilesById[scout.Id] = scout;
        _profilesById[planner.Id] = planner;
        _profilesById[engineer.Id] = engineer;
        _profilesById[verifier.Id] = verifier;

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

        StatusMessage = "已创建示例 Team（Architecture Optimizer）。";
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
        CancellationToken cancellationToken = default,
        IProgress<TeamRunProgress>? progress = null)
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
                    humanAnswer.Trim(),
                    progress)
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
        CancellationToken cancellationToken = default,
        IProgress<TeamRunProgress>? progress = null)
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
                .RunToCompletionAsync(
                    began.Task.RootSessionId,
                    began.Task.TaskId,
                    runCts.Token,
                    humanAnswer: null,
                    progress)
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

    public IReadOnlyList<ModifiedFileViewModel> CollectChatFiles(TeamTask task) =>
        TeamChatArtifacts.CollectTaskFiles(task, _taskStore);

    public Task<TeamTask?> GetTaskSnapshotAsync(
        string rootSessionId,
        string taskId,
        CancellationToken cancellationToken = default) =>
        _taskStore.GetAsync(rootSessionId, taskId, cancellationToken);

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
            ToolOptions.Add(new CapabilityOptionViewModel(tool, tool, isChecked: false, isEnabled: true));
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
            // Only real knowledge spaces — no demo/fallback placeholders.
            foreach (var module in await _knowledgeStore.ListModulesAsync().ConfigureAwait(true))
            {
                KnowledgeOptions.Add(new CapabilityOptionViewModel(module.Module.Id, module.Module.Name, false));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to list knowledge modules for Teams page: {Message}", ex.Message);
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
            if (isLeader && profile is not null)
            {
                ApplyFixedLeaderCapabilities(profile);
            }

            var allow = profile is null
                ? Array.Empty<string>()
                : (isLeader
                    ? TeamToolDefaults.DefaultLeaderAllowList.ToArray()
                    : TeamToolDefaults.ResolveAllowList(profile, isLeader: false).ToArray());

            foreach (var option in ToolOptions)
            {
                option.IsEnabled = !isLeader;
                if (!option.IsEnabled)
                {
                    option.IsChecked = false;
                    continue;
                }

                option.IsChecked = TeamToolDefaults.MatchesAllowList(option.Id, allow);
            }

            foreach (var option in SkillOptions)
            {
                option.IsChecked = !isLeader
                    && profile?.SkillNames.Contains(option.Id, StringComparer.OrdinalIgnoreCase) == true;
            }

            foreach (var option in McpOptions)
            {
                option.IsChecked = !isLeader
                    && profile?.McpServerNames.Contains(option.Id, StringComparer.OrdinalIgnoreCase) == true;
            }

            foreach (var option in KnowledgeOptions)
            {
                option.IsChecked = !isLeader
                    && profile?.KnowledgeModuleIds.Contains(option.Id, StringComparer.OrdinalIgnoreCase) == true;
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

        if (SelectedMember?.IsLeader == true)
        {
            ApplyFixedLeaderCapabilities(SelectedProfile);
            SelectedMember.RefreshCaps(isLeader: true);
            return;
        }

        var catalogIds = new HashSet<string>(
            ToolOptions.Select(o => o.Id),
            StringComparer.OrdinalIgnoreCase);
        var impliedIds = new HashSet<string>(
            TeamToolDefaults.SharedTeamTools
                .Append(TeamToolDefaults.LoadSkillThroughPathTool)
                .Append(TeamToolDefaults.KnowledgeSearchTool),
            StringComparer.OrdinalIgnoreCase);
        var checkedCatalog = ToolOptions
            .Where(o => o.IsEnabled && o.IsChecked)
            .Select(o => o.Id)
            .ToList();
        // Preserve tools that exist on the profile but are not shown in the catalog
        // (avoids silently stripping extras after older catalog gaps).
        // Never preserve Leader orchestration / implied tools; those are re-injected below.
        var preserved = SelectedProfile.ToolAllowList
            .Where(t =>
                !string.IsNullOrWhiteSpace(t)
                && !TeamToolDefaults.IsLeaderOrchestrationEntry(t)
                && !impliedIds.Contains(t)
                && !catalogIds.Contains(t)
                && !MatchesAnyCatalogWildcard(t, catalogIds))
            .ToList();
        SelectedProfile.SkillNames = SkillOptions.Where(o => o.IsChecked).Select(o => o.Id).ToList();
        SelectedProfile.McpServerNames = McpOptions.Where(o => o.IsChecked).Select(o => o.Id).ToList();
        SelectedProfile.KnowledgeModuleIds = KnowledgeOptions.Where(o => o.IsChecked).Select(o => o.Id).ToList();
        SelectedProfile.ToolAllowList = TeamToolDefaults.EnsureWorkerImpliedTools(
            checkedCatalog.Concat(preserved),
            SelectedProfile.SkillNames,
            SelectedProfile.KnowledgeModuleIds).ToList();
        SelectedMember?.RefreshCaps(SelectedMember.IsLeader);
    }

    private static void ApplyFixedLeaderCapabilities(AgentProfile profile)
    {
        profile.ToolAllowList = TeamToolDefaults.DefaultLeaderAllowList.ToList();
        profile.SkillNames = [];
        profile.McpServerNames = [];
        profile.KnowledgeModuleIds = [];
    }

    private static bool MatchesAnyCatalogWildcard(string toolName, HashSet<string> catalogIds)
    {
        foreach (var id in catalogIds)
        {
            if (!id.EndsWith('*'))
            {
                continue;
            }

            var prefix = id[..^1];
            if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
