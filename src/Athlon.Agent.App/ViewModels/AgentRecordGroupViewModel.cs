using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    [ObservableProperty]
    private bool isExpanded;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>Segoe Fluent Icons: Cloud for remote; OpenFolder / Folder for local.</summary>
    public string FolderGlyph => IsRemote
        ? "\uE753"
        : IsExpanded ? "\uE838" : "\uE8B7";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandGlyph));
        OnPropertyChanged(nameof(FolderGlyph));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
