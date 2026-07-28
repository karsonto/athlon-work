using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SkillSidebarItemViewModel : ObservableObject
{
    private readonly Func<string, Task>? _onActivate;

    public SkillSidebarItemViewModel(string name, bool isEnabled, Func<string, Task>? onActivate = null)
    {
        Name = name;
        IsEnabled = isEnabled;
        _onActivate = onActivate;
    }

    public string Name { get; }

    public bool IsEnabled { get; }

    public bool IsDisabled => !IsEnabled;

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (_onActivate is null)
        {
            return;
        }

        await _onActivate(Name).ConfigureAwait(true);
    }
}
