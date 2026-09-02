using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class AgentRecordGroupViewModel : ObservableObject
{
    public AgentRecordGroupViewModel(
        string key,
        string title,
        bool isExpandedByDefault,
        string? workspacePath = null,
        string? activeWorkspaceId = null,
        bool isRemote = false)
    {
        Key = key;
        Title = title;
        WorkspacePath = workspacePath;
        ActiveWorkspaceId = activeWorkspaceId;
        IsRemote = isRemote;
        IsExpanded = isExpandedByDefault;
    }

    public string Key { get; }
    public string Title { get; }
    public string? WorkspacePath { get; }
    public string? ActiveWorkspaceId { get; }
    public bool IsRemote { get; }
    public ObservableCollection<SessionHistoryItemViewModel> Items { get; } = new();
    public bool HasItems => Items.Count > 0;
    public bool HasWorkspace => !string.IsNullOrWhiteSpace(WorkspacePath);

    public bool HasRunningSessions { get; private set; }

    public int RunningSessionCount { get; private set; }

    public string? RunningBrushKey { get; private set; }

    public void ApplyRunningSummary()
    {
        var running = Items.Where(item => item.IsRunning).ToList();
        HasRunningSessions = running.Count > 0;
        RunningSessionCount = running.Count;
        RunningBrushKey = running.FirstOrDefault()?.RunningBrushKey;
        OnPropertyChanged(nameof(HasRunningSessions));
        OnPropertyChanged(nameof(RunningSessionCount));
        OnPropertyChanged(nameof(RunningBrushKey));
        OnPropertyChanged(nameof(HeaderForegroundBrushKey));
        OnPropertyChanged(nameof(FolderGlyphBrushKey));
    }

    public string HeaderForegroundBrushKey =>
        HasRunningSessions && RunningBrushKey is not null
            ? RunningBrushKey
            : "Brush.Text";

    public string FolderGlyphBrushKey =>
        HasRunningSessions && RunningBrushKey is not null
            ? RunningBrushKey
            : "Brush.SubtleText";

    [ObservableProperty]
    private bool isExpanded;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>Material icon for remote cloud vs local folder state.</summary>
    public PackIconKind FolderIconKind => IsRemote
        ? PackIconKind.CloudOutline
        : IsExpanded ? PackIconKind.FolderOpenOutline : PackIconKind.FolderOutline;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandGlyph));
        OnPropertyChanged(nameof(FolderIconKind));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
