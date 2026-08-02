using System.Collections.ObjectModel;
using Athlon.Agent.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class WorkspacePaneViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private int _browserSerial;
    private int _terminalSerial;

    public WorkspacePaneViewModel(ILocalizationService localization)
    {
        _loc = localization;
        Tabs = new ObservableCollection<WorkspaceTabViewModel>();
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; }

    [ObservableProperty]
    private WorkspaceTabViewModel? activeTab;

    [ObservableProperty]
    private bool isAddMenuOpen;

    public bool HasTabs => Tabs.Count > 0;

    public bool IsEmpty => Tabs.Count == 0;

    partial void OnActiveTabChanged(WorkspaceTabViewModel? value)
    {
        IsAddMenuOpen = false;
    }

    [RelayCommand]
    private void AddFilesTab()
    {
        var existing = Tabs.OfType<FilesWorkspaceTabViewModel>().FirstOrDefault();
        if (existing is not null)
        {
            ActiveTab = existing;
            IsAddMenuOpen = false;
            return;
        }

        var tab = new FilesWorkspaceTabViewModel(_loc["Context_TabFiles"]);
        Tabs.Add(tab);
        ActiveTab = tab;
        IsAddMenuOpen = false;
    }

    [RelayCommand]
    private void AddSkillsTab()
    {
        var existing = Tabs.OfType<SkillsWorkspaceTabViewModel>().FirstOrDefault();
        if (existing is not null)
        {
            ActiveTab = existing;
            IsAddMenuOpen = false;
            return;
        }

        var tab = new SkillsWorkspaceTabViewModel(_loc["Context_TabSkills"]);
        Tabs.Add(tab);
        ActiveTab = tab;
        IsAddMenuOpen = false;
    }

    [RelayCommand]
    private void AddBrowserTab()
    {
        _browserSerial++;
        var title = _browserSerial <= 1
            ? _loc["Workspace_Browser"]
            : string.Format(_loc["Workspace_BrowserN"], _browserSerial);
        var tab = new BrowserWorkspaceTabViewModel(title);
        Tabs.Add(tab);
        ActiveTab = tab;
        IsAddMenuOpen = false;
    }

    [RelayCommand]
    private void AddTerminalTab()
    {
        _terminalSerial++;
        var title = _terminalSerial <= 1
            ? _loc["Workspace_Terminal"]
            : string.Format(_loc["Workspace_TerminalN"], _terminalSerial);
        var tab = new TerminalWorkspaceTabViewModel(title);
        Tabs.Add(tab);
        ActiveTab = tab;
        IsAddMenuOpen = false;
    }

    [RelayCommand]
    private void ToggleAddMenu() => IsAddMenuOpen = !IsAddMenuOpen;

    [RelayCommand]
    private void CloseTab(WorkspaceTabViewModel? tab)
    {
        if (tab is null || !tab.CanClose)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count == 0
                ? null
                : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }
    }
}
