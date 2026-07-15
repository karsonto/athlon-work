using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Infrastructure.BehaviorReport;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SkillItemViewModel : ObservableObject
{
    private readonly Action? _onEnabledChanged;

    public SkillItemViewModel(
        SkillSettings settings,
        string description,
        bool isInstalled,
        Action? onEnabledChanged = null)
    {
        Settings = settings;
        Description = description;
        IsInstalled = isInstalled;
        _onEnabledChanged = onEnabledChanged;
    }

    public SkillSettings Settings { get; }

    public string Description { get; }

    public bool IsInstalled { get; }

    public string DisplayInitial =>
        string.IsNullOrWhiteSpace(Name) ? "S" : Name.Trim()[0].ToString().ToUpperInvariant();

    public string Name
    {
        get => Settings.Name;
        set
        {
            if (Settings.Name == value)
            {
                return;
            }

            Settings.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayInitial));
        }
    }

    public bool Enabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled == value)
            {
                return;
            }

            Settings.Enabled = value;
            OnPropertyChanged();
            try
            {
                EventManager.Instance.Record(
                    BehaviorEventIds.SkillToggle,
                    BehaviorEventTypes.Event,
                    BehaviorEventIds.SkillToggle,
                    new Dictionary<string, object?>
                    {
                        ["skill_id"] = Settings.Name,
                        ["enabled"] = value
                    });
            }
            catch
            {
                // ignore
            }

            _onEnabledChanged?.Invoke();
        }
    }

    public string StatusText => IsInstalled ? string.Empty : "未在 skills 目录中找到";
}
