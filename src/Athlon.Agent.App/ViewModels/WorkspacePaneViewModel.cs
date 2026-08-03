using System.Collections.ObjectModel;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class WorkspacePaneViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly AppSettings _appSettings;
    private int _browserSerial;
    private int _terminalSerial;

    public WorkspacePaneViewModel(
        ILocalizationService localization,
        IActiveWorkspaceContext workspaceContext,
        AppSettings appSettings)
    {
        _loc = localization;
        _workspaceContext = workspaceContext;
        _appSettings = appSettings;
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
    private void AddBrowserTab() => AddBrowserTabAndActivate();

    /// <summary>Creates a Browser tab, activates it, and returns the view model (used by automation host).</summary>
    public BrowserWorkspaceTabViewModel AddBrowserTabAndActivate()
    {
        _browserSerial++;
        var title = _browserSerial <= 1
            ? _loc["Workspace_Browser"]
            : string.Format(_loc["Workspace_BrowserN"], _browserSerial);
        var tab = new BrowserWorkspaceTabViewModel(title);
        Tabs.Add(tab);
        ActiveTab = tab;
        IsAddMenuOpen = false;
        return tab;
    }

    [RelayCommand]
    private void AddTerminalTab()
    {
        _terminalSerial++;
        var title = _terminalSerial <= 1
            ? _loc["Workspace_Terminal"]
            : string.Format(_loc["Workspace_TerminalN"], _terminalSerial);
        var cwd = WorkspaceTerminalBootstrap.ResolveWorkingDirectory(_workspaceContext);
        var tab = new TerminalWorkspaceTabViewModel(title, cwd)
        {
            StartupCommandLine = WorkspaceTerminalBootstrap.ResolveStartupCommandLine(
                _appSettings.Ui.TerminalShell)
        };
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

        var wasActive = ReferenceEquals(ActiveTab, tab);
        Tabs.RemoveAt(index);
        if (wasActive)
        {
            // Switch content first so the terminal view can disconnect and hand back its ConPTY session.
            ActiveTab = Tabs.Count == 0
                ? null
                : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        if (tab is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
