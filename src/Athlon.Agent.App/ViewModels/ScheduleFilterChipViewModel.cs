using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ScheduleFilterChipViewModel : ObservableObject
{
    public ScheduleFilterChipViewModel(string label, int index)
    {
        Label = label;
        Index = index;
    }

    public string Label { get; }
    public int Index { get; }

    [ObservableProperty]
    private bool isSelected;
}
