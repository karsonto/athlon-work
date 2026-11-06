using System.Collections.ObjectModel;
using System.Windows;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Storage;
using Athlon.Agent.Core.Teams;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class TeamsPageViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IAppSettingsRepository _settingsRepository;
    private readonly ITeamDefinitionStore _teamStore;
    private readonly IAgentProfileStore _profileStore;
    private readonly ITeamTaskStore _taskStore;
    private readonly ITeamTraceStore _traceStore;
    private readonly ITeamTaskOrchestrator _orchestrator;
    private readonly IActiveAgentSessionContext _activeSession;
    private readonly IAppLogger _logger;

    public TeamsPageViewModel(
        AppSettings settings,
        IAppSettingsRepository settingsRepository,
        ITeamDefinitionStore teamStore,
        IAgentProfileStore profileStore,
        ITeamTaskStore taskStore,
        ITeamTraceStore traceStore,
        ITeamTaskOrchestrator orchestrator,
        IActiveAgentSessionContext activeSession,
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
        _logger = logger.ForContext("TeamsPage");
        TeamsEnabled = settings.Teams.Enabled;
    }

    public ObservableCollection<TeamDefinition> Teams { get; } = new();
    public ObservableCollection<AgentProfile> Profiles { get; } = new();
    public ObservableCollection<TeamTask> RecentTasks { get; } = new();

    [ObservableProperty] private bool _teamsEnabled;
    [ObservableProperty] private TeamDefinition? _selectedTeam;
    [ObservableProperty] private AgentProfile? _selectedProfile;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _runGoal = "整理一次发版材料摘要";
    [ObservableProperty] private bool _isBusy;

    partial void OnTeamsEnabledChanged(bool value)
    {
        _settings.Teams.Enabled = value;
    }

    public async Task RefreshAsync()
    {
        Teams.Clear();
        Profiles.Clear();
        RecentTasks.Clear();

        foreach (var team in await _teamStore.ListAsync().ConfigureAwait(true))
        {
            Teams.Add(team);
        }

        foreach (var profile in await _profileStore.ListAsync().ConfigureAwait(true))
        {
            Profiles.Add(profile);
        }

        SelectedTeam ??= Teams.FirstOrDefault();
        SelectedProfile ??= Profiles.FirstOrDefault();

        var sessionId = _activeSession.SessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            foreach (var task in await _taskStore.ListForRootSessionAsync(sessionId).ConfigureAwait(true))
            {
                RecentTasks.Add(task);
            }
        }

        StatusMessage = TeamsEnabled
            ? $"{Teams.Count} teams · {Profiles.Count} profiles"
            : "Teams disabled — enable and Save, then run a team task.";
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _settings.Teams.Enabled = TeamsEnabled;
        await _settingsRepository.SaveSettingsAsync(_settings).ConfigureAwait(true);
        StatusMessage = "Saved Teams settings.";
    }

    [RelayCommand]
    private async Task CreateSampleTeamAsync()
    {
        var leader = new AgentProfile
        {
            Id = "profile-leader-release",
            Name = "Release Leader",
            Description = "Orchestrates release prep workers",
            AgentMd = "You are the release team leader. Prefer team_* tools. Redispatch QA when coverage is incomplete.",
            AllowRedispatch = false,
            MaxIterations = 1
        };
        var eng = new AgentProfile
        {
            Id = "profile-worker-eng",
            Name = "Engineering Notes",
            Description = "Summarize code changes",
            AgentMd = "Produce concise engineering notes only.",
            AllowRedispatch = false,
            MaxIterations = 1
        };
        var qa = new AgentProfile
        {
            Id = "profile-worker-qa",
            Name = "QA Summary",
            Description = "Summarize test results",
            AgentMd = "Produce QA conclusions; list blockers.",
            AllowRedispatch = true,
            MaxIterations = 3
        };

        await _profileStore.SaveAsync(leader).ConfigureAwait(true);
        await _profileStore.SaveAsync(eng).ConfigureAwait(true);
        await _profileStore.SaveAsync(qa).ConfigureAwait(true);

        var team = new TeamDefinition
        {
            Id = "team-release-prep",
            Name = "Release Prep Squad",
            Description = "Leader + Eng + QA sample team",
            LeaderProfileId = leader.Id,
            WorkerProfileIds = [eng.Id, qa.Id],
            MaxParallelWorkers = 2
        };
        await _teamStore.SaveAsync(team).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SelectedTeam = Teams.FirstOrDefault(t => t.Id == team.Id);
        StatusMessage = "Sample team created.";
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        await _profileStore.SaveAsync(SelectedProfile).ConfigureAwait(true);
        StatusMessage = $"Saved profile {SelectedProfile.Name}.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveTeamAsync()
    {
        if (SelectedTeam is null)
        {
            return;
        }

        await _teamStore.SaveAsync(SelectedTeam).ConfigureAwait(true);
        StatusMessage = $"Saved team {SelectedTeam.Name}.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunSelectedTeamAsync()
    {
        if (!TeamsEnabled)
        {
            StatusMessage = "Enable Teams first.";
            return;
        }

        if (SelectedTeam is null)
        {
            StatusMessage = "Select a team.";
            return;
        }

        var sessionId = _activeSession.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            StatusMessage = "Open a chat session first (root session id required).";
            return;
        }

        if (string.IsNullOrWhiteSpace(RunGoal))
        {
            StatusMessage = "Enter a goal.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Running team task…";
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
            StatusMessage = "No recent tasks for this session.";
            return;
        }

        await OpenTraceAsync(latest).ConfigureAwait(true);
    }

    public async Task OpenTraceAsync(TeamTask task)
    {
        var events = await _traceStore.ReadAsync(task.RootSessionId, task.TaskId).ConfigureAwait(true);
        var owner = Application.Current?.MainWindow;
        TeamTraceWindow.ShowOrFocus(task, events, owner, _traceStore);
    }
}
