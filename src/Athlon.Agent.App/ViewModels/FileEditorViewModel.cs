using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Athlon.Agent.App.Localization;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class FileEditorViewModel : ObservableObject
{
    private readonly WorkspaceFileEditorService _editorService;
    private readonly WorkspaceGuard _guard;
    private readonly ILocalizationService _loc;
    private readonly IUserNotifier _notifier;
    private EditorDocumentViewModel? _activeDocument;

    public FileEditorViewModel(
        WorkspaceFileEditorService editorService,
        WorkspaceGuard guard,
        ILocalizationService localization,
        IUserNotifier notifier)
    {
        _editorService = editorService;
        _guard = guard;
        _loc = localization;
        _notifier = notifier;
        Tabs = new ObservableCollection<EditorDocumentViewModel>();
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOpenTabs));
            OnPropertyChanged(nameof(IsPaneVisible));
            OnPropertyChanged(nameof(ShowPlanBuildButton));
        };
    }

    public ObservableCollection<EditorDocumentViewModel> Tabs { get; }

    public EditorDocumentViewModel? ActiveDocument
    {
        get => _activeDocument;
        set
        {
            if (ReferenceEquals(_activeDocument, value))
            {
                return;
            }

            if (_activeDocument is not null)
            {
                _activeDocument.PropertyChanged -= OnActiveDocumentPropertyChanged;
            }

            if (!SetProperty(ref _activeDocument, value))
            {
                return;
            }

            if (_activeDocument is not null)
            {
                _activeDocument.PropertyChanged += OnActiveDocumentPropertyChanged;
            }

            OnPropertyChanged(nameof(ShowPlanBuildButton));
        }
    }

    public bool HasOpenTabs => Tabs.Count > 0;

    public bool IsPaneVisible => HasOpenTabs;

    public bool HasUnsavedChanges => Tabs.Any(tab => tab.IsDirty);

    public bool ShowPlanBuildButton => ActiveDocument?.CanBuild == true;

    private void OnActiveDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorDocumentViewModel.CanBuild)
            or nameof(EditorDocumentViewModel.IsSessionPlan))
        {
            OnPropertyChanged(nameof(ShowPlanBuildButton));
        }
    }

    public void OpenOrUpdateSessionPlan(SessionPlan plan, string sessionId, bool activateTab = true)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || plan is null || !plan.HasContent)
        {
            return;
        }

        var path = EditorDocumentViewModel.BuildSessionPlanPath(sessionId);
        var existing = Tabs.FirstOrDefault(tab =>
            tab.IsSessionPlan
            && string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.ApplySessionPlan(plan);
            if (activateTab || ReferenceEquals(ActiveDocument, existing))
            {
                ActiveDocument = existing;
            }

            OnPropertyChanged(nameof(ShowPlanBuildButton));
            return;
        }

        var document = EditorDocumentViewModel.CreateSessionPlan(sessionId, plan);
        Tabs.Add(document);
        if (activateTab || ActiveDocument is null)
        {
            ActiveDocument = document;
        }

        OnPropertyChanged(nameof(ShowPlanBuildButton));
    }

    public void CloseSessionPlanTab(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var path = EditorDocumentViewModel.BuildSessionPlanPath(sessionId);
        var document = Tabs.FirstOrDefault(tab =>
            tab.IsSessionPlan
            && string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            return;
        }

        var index = Tabs.IndexOf(document);
        var wasActive = ReferenceEquals(ActiveDocument, document);
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0)
        {
            ActiveDocument = null;
            return;
        }

        if (wasActive)
        {
            ActiveDocument = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }
    }

    public async Task<bool> OpenFileAsync(string path, string? workspaceRoot, bool readOnly = false)
    {
        var fullPath = NormalizeEditorPath(path);
        var existing = Tabs.FirstOrDefault(tab => PathsEqual(tab.FilePath, fullPath));
        if (existing is not null)
        {
            existing.IsReadOnly = readOnly;
            ActiveDocument = existing;
            return true;
        }

        var result = await _editorService.TryOpenAsync(fullPath).ConfigureAwait(true);
        if (!result.Succeeded || result.Content is null || result.FullPath is null)
        {
            if (result.ErrorMessage is null)
            {
                _notifier.Info("Editor_CannotOpenTitle", "Editor_CannotOpenMessage");
            }
            else
            {
                _notifier.WarningText("Editor_CannotOpenTitle", result.ErrorMessage);
            }

            return false;
        }

        var relative = TryGetRelativePath(workspaceRoot, result.FullPath);
        var document = new EditorDocumentViewModel(result.FullPath, result.Content, relative, readOnly);
        Tabs.Add(document);
        ActiveDocument = document;
        return true;
    }

    public EditorDocumentViewModel? FindOpenDocument(string fullPath)
    {
        var normalized = NormalizeEditorPath(fullPath);
        return Tabs.FirstOrDefault(tab => PathsEqual(tab.FilePath, normalized));
    }

    [RelayCommand]
    private void SetViewMode(EditorViewMode mode)
    {
        if (ActiveDocument is null || !ActiveDocument.CanPreview)
        {
            return;
        }

        ActiveDocument.ViewMode = mode;
    }

    [RelayCommand]
    private async Task SaveActiveAsync()
    {
        if (ActiveDocument is null || ActiveDocument.IsSessionPlan)
        {
            return;
        }

        await SaveDocumentAsync(ActiveDocument).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CloseTab(EditorDocumentViewModel? document)
    {
        await CloseTabAsync(document).ConfigureAwait(true);
    }

    public async Task CloseTabAsync(EditorDocumentViewModel? document)
    {
        document ??= ActiveDocument;
        if (document is null)
        {
            return;
        }

        if (document.IsDirty && !document.IsSessionPlan)
        {
            var answer = _notifier.AskYesNoCancel("Editor_UnsavedTitle", "Editor_UnsavedMessage", document.DisplayName);
            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                var saved = await SaveDocumentAsync(document).ConfigureAwait(true);
                if (!saved)
                {
                    return;
                }
            }
        }

        var index = Tabs.IndexOf(document);
        if (index < 0)
        {
            return;
        }

        // Capture before Remove: ListBox TwoWay SelectedItem clears ActiveDocument when the
        // selected item leaves the collection, so post-Remove "ActiveDocument == document" fails.
        var wasActive = ReferenceEquals(ActiveDocument, document);
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0)
        {
            ActiveDocument = null;
            return;
        }

        if (wasActive)
        {
            ActiveDocument = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }
    }

    public async Task<bool> SaveDocumentAsync(EditorDocumentViewModel document)
    {
        if (document.IsSessionPlan)
        {
            return true;
        }

        var result = await _editorService.SaveAsync(document.FilePath, document.Content).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            if (result.ErrorMessage is null)
            {
                _notifier.Warning("Editor_SaveFailedTitle", "Editor_SaveFailedMessage");
            }
            else
            {
                _notifier.WarningText("Editor_SaveFailedTitle", result.ErrorMessage);
            }

            return false;
        }

        document.MarkSaved(document.Content);
        return true;
    }

    public async Task<bool> TryCloseAllTabsAsync()
    {
        while (Tabs.Count > 0)
        {
            var document = Tabs[0];
            if (document.IsDirty && !document.IsSessionPlan)
            {
                var answer = _notifier.AskYesNoCancel("Editor_UnsavedTitle", "Editor_UnsavedMessage", document.DisplayName);
                if (answer == MessageBoxResult.Cancel)
                {
                    return false;
                }

                if (answer == MessageBoxResult.Yes)
                {
                    var saved = await SaveDocumentAsync(document).ConfigureAwait(true);
                    if (!saved)
                    {
                        return false;
                    }
                }
            }

            Tabs.RemoveAt(0);
        }

        ActiveDocument = null;
        return true;
    }

    public void HandleExternalFileChange(string fullPath)
    {
        if (_guard.CurrentKind == WorkspaceKind.Ssh)
        {
            return;
        }

        var document = Tabs.FirstOrDefault(tab =>
            string.Equals(tab.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (document is null || document.IsSessionPlan || !File.Exists(fullPath))
        {
            return;
        }

        if (document.IsDirty)
        {
            return;
        }

        try
        {
            var content = File.ReadAllText(fullPath);
            document.ReloadFromDisk(content);
        }
        catch
        {
            // Ignore reload failures; user can reopen manually.
        }
    }

    private string NormalizeEditorPath(string path)
    {
        if (EditorDocumentViewModel.IsSessionPlanPath(path))
        {
            return path;
        }

        return _guard.CurrentKind == WorkspaceKind.Ssh
            ? _guard.Normalize(path)
            : Path.GetFullPath(path);
    }

    private bool PathsEqual(string left, string right) =>
        EditorDocumentViewModel.IsSessionPlanPath(left) || EditorDocumentViewModel.IsSessionPlanPath(right)
            ? string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            : _guard.CurrentKind == WorkspaceKind.Ssh
                ? string.Equals(
                    RemotePathNormalizer.Collapse(RemotePathNormalizer.ForModel(left)),
                    RemotePathNormalizer.Collapse(RemotePathNormalizer.ForModel(right)),
                    StringComparison.Ordinal)
                : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private string? TryGetRelativePath(string? workspaceRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        try
        {
            if (_guard.CurrentKind == WorkspaceKind.Ssh)
            {
                var root = RemotePathNormalizer.NormalizeRoot(workspaceRoot);
                var normalized = RemotePathNormalizer.Collapse(RemotePathNormalizer.ForModel(fullPath));
                if (!RemotePathNormalizer.IsUnderRoot(normalized, root))
                {
                    return null;
                }

                if (string.Equals(normalized, root, StringComparison.Ordinal))
                {
                    return ".";
                }

                var prefix = root.TrimEnd('/') + "/";
                return normalized.StartsWith(prefix, StringComparison.Ordinal)
                    ? normalized[prefix.Length..]
                    : null;
            }

            var localRoot = Path.GetFullPath(workspaceRoot);
            return Path.GetRelativePath(localRoot, fullPath).Replace('\\', '/');
        }
        catch
        {
            return null;
        }
    }
}
