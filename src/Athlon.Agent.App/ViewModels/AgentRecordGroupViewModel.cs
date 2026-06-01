using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class AgentRecordGroupViewModel : ObservableObject
{
    public AgentRecordGroupViewModel(string key, string title, bool isExpandedByDefault)
    {
        Key = key;
        Title = title;
        IsExpanded = isExpandedByDefault;
    }

    public string Key { get; }
    public string Title { get; }
    public ObservableCollection<SessionHistoryItemViewModel> Items { get; } = new();
    public bool HasItems => Items.Count > 0;

    [ObservableProperty]
    private bool isExpanded;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
