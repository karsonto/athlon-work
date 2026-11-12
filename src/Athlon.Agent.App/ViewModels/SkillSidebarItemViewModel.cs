using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SkillSidebarItemViewModel : ObservableObject
{
    private readonly Func<string, Task>? _onToggle;

    public SkillSidebarItemViewModel(
        string name,
        string? description,
        string? directoryPath,
        bool isEnabled,
        Func<string, Task>? onToggle = null)
    {
        Name = name;
        Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim();
        DirectoryPath = string.IsNullOrWhiteSpace(directoryPath) ? string.Empty : directoryPath.Trim();
        IsEnabled = isEnabled;
        _onToggle = onToggle;
    }

    public string Name { get; }

    public string Description { get; }

    public string DirectoryPath { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasDirectory => !string.IsNullOrWhiteSpace(DirectoryPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisabled))]
    private bool isEnabled;

    public bool IsDisabled => !IsEnabled;

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_onToggle is null)
        {
            return;
        }

        await _onToggle(Name).ConfigureAwait(true);
    }
}
