using System.Collections.ObjectModel;
using Athlon.Agent.Core.Teams;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class TeamListItemViewModel : ObservableObject
{
    public TeamListItemViewModel(TeamDefinition team)
    {
        Team = team;
        RefreshMeta();
    }

    public TeamDefinition Team { get; }

    public string Name => string.IsNullOrWhiteSpace(Team.Name) ? "(未命名 Team)" : Team.Name;

    [ObservableProperty] private string _meta = "";

    [ObservableProperty] private bool _isSelected;

    public void RefreshMeta()
    {
        var workers = Team.WorkerProfileIds?.Count ?? 0;
        Meta = $"1 Leader · {workers} Workers · 并行 {Math.Max(1, Team.MaxParallelWorkers)}";
        OnPropertyChanged(nameof(Name));
    }
}

public sealed partial class CapChipViewModel : ObservableObject
{
    public CapChipViewModel(string text, bool isOn)
    {
        Text = text;
        IsOn = isOn;
    }

    public string Text { get; }

    [ObservableProperty] private bool _isOn;
}

public sealed partial class TeamMemberItemViewModel : ObservableObject
{
    public TeamMemberItemViewModel(AgentProfile profile, bool isLeader)
    {
        Profile = profile;
        IsLeader = isLeader;
        Initials = BuildInitials(profile.Name);
        RefreshCaps(isLeader);
    }

    public AgentProfile Profile { get; }

    public bool IsLeader { get; }

    public string RoleLabel => IsLeader ? "Leader" : "Worker";

    public string Initials { get; private set; }

    public string Subtitle =>
        string.IsNullOrWhiteSpace(Profile.Description)
            ? (IsLeader ? "统筹拆解 · 分派 · 汇总验收" : "执行分派任务")
            : Profile.Description;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Profile.Name) ? (IsLeader ? "Leader" : "Worker") : Profile.Name;

    public ObservableCollection<CapChipViewModel> Caps { get; } = new();

    [ObservableProperty] private bool _isSelected;

    public void RefreshFromProfile()
    {
        Initials = BuildInitials(Profile.Name);
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
        RefreshCaps(IsLeader);
    }

    public void RefreshCaps(bool isLeader)
    {
        Caps.Clear();
        var allow = TeamToolDefaults.ResolveAllowList(Profile, isLeader);
        var hasLeaderTeam = allow.Any(t =>
            t.StartsWith("team_create", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("team_dispatch", StringComparison.OrdinalIgnoreCase)
            || t.Equals("team_*", StringComparison.OrdinalIgnoreCase));
        var hasFile = allow.Any(t => t.StartsWith("file_", StringComparison.OrdinalIgnoreCase) || t.StartsWith("file*", StringComparison.OrdinalIgnoreCase));
        var hasSearch = allow.Any(t =>
            t.Contains("grep", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("glob", StringComparison.OrdinalIgnoreCase));
        var hasExec = allow.Any(t => t.Equals("execute_command", StringComparison.OrdinalIgnoreCase));

        if (isLeader)
        {
            Caps.Add(new CapChipViewModel("team_*", hasLeaderTeam));
        }

        // Shared team tools are mandatory for every member.
        Caps.Add(new CapChipViewModel("progress/memory", true));

        if (hasFile)
        {
            Caps.Add(new CapChipViewModel("file_*", true));
        }

        if (hasSearch)
        {
            Caps.Add(new CapChipViewModel("grep/glob", true));
        }

        if (hasExec)
        {
            Caps.Add(new CapChipViewModel("execute_command", true));
        }

        Caps.Add(new CapChipViewModel($"skills×{Profile.SkillNames.Count}", Profile.SkillNames.Count > 0));
        Caps.Add(new CapChipViewModel($"mcp×{Profile.McpServerNames.Count}", Profile.McpServerNames.Count > 0));
        Caps.Add(new CapChipViewModel($"kb×{Profile.KnowledgeModuleIds.Count}", Profile.KnowledgeModuleIds.Count > 0));
    }

    private static string BuildInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "AG";
        }

        var parts = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
        }

        return name.Length >= 2
            ? name[..2].ToUpperInvariant()
            : name.ToUpperInvariant();
    }
}

public sealed partial class CapabilityOptionViewModel : ObservableObject
{
    public CapabilityOptionViewModel(string id, string label, bool isChecked, bool isEnabled = true)
    {
        Id = id;
        Label = label;
        IsChecked = isChecked;
        IsEnabled = isEnabled;
    }

    public string Id { get; }

    public string Label { get; }

    [ObservableProperty] private bool _isEnabled;

    [ObservableProperty] private bool _isChecked;
}

public sealed partial class OrgPreviewNodeViewModel : ObservableObject
{
    public OrgPreviewNodeViewModel(string name, bool isLeader)
    {
        Name = name;
        IsLeader = isLeader;
        RoleLabel = isLeader ? "Leader" : "W";
    }

    public string Name { get; }

    public bool IsLeader { get; }

    public string RoleLabel { get; }
}
