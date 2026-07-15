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
        string? workspacePath = null)
    {
        Key = key;
        Title = title;
        WorkspacePath = workspacePath;
        IsExpanded = isExpandedByDefault;
    }

    public string Key { get; }
    public string Title { get; }
    public string? WorkspacePath { get; }
    public ObservableCollection<SessionHistoryItemViewModel> Items { get; } = new();
    public bool HasItems => Items.Count > 0;
    public bool HasWorkspace => !string.IsNullOrWhiteSpace(WorkspacePath);

    [ObservableProperty]
    private bool isExpanded;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>Segoe Fluent Icons: OpenFolder / Folder.</summary>
    public string FolderGlyph => IsExpanded ? "\uE838" : "\uE8B7";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandGlyph));
        OnPropertyChanged(nameof(FolderGlyph));
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
