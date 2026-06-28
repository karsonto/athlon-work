using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Athlon.Agent.App.Localization;
using Athlon.Agent.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class FileEditorViewModel : ObservableObject
{
    private readonly WorkspaceFileEditorService _editorService;
    private readonly ILocalizationService _loc;
    private readonly IUserNotifier _notifier;
    private EditorDocumentViewModel? _activeDocument;

    public FileEditorViewModel(
        WorkspaceFileEditorService editorService,
        ILocalizationService localization,
        IUserNotifier notifier)
    {
        _editorService = editorService;
        _loc = localization;
        _notifier = notifier;
        Tabs = new ObservableCollection<EditorDocumentViewModel>();
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOpenTabs));
            OnPropertyChanged(nameof(IsPaneVisible));
        };
    }

    public ObservableCollection<EditorDocumentViewModel> Tabs { get; }

    public EditorDocumentViewModel? ActiveDocument
    {
        get => _activeDocument;
        set => SetProperty(ref _activeDocument, value);
    }

    public bool HasOpenTabs => Tabs.Count > 0;

    public bool IsPaneVisible => HasOpenTabs;

    public bool HasUnsavedChanges => Tabs.Any(tab => tab.IsDirty);

    public async Task<bool> OpenFileAsync(string path, string? workspaceRoot, bool readOnly = false)
    {
        var fullPath = Path.GetFullPath(path);
        var existing = Tabs.FirstOrDefault(tab =>
            string.Equals(tab.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
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
        var normalized = Path.GetFullPath(fullPath);
        return Tabs.FirstOrDefault(tab =>
            string.Equals(tab.FilePath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private async Task SaveActiveAsync()
    {
        if (ActiveDocument is null)
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

        if (document.IsDirty)
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
        Tabs.Remove(document);
        if (Tabs.Count == 0)
        {
            ActiveDocument = null;
            return;
        }

        if (ActiveDocument == document)
        {
            ActiveDocument = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    public async Task<bool> SaveDocumentAsync(EditorDocumentViewModel document)
    {
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
            if (document.IsDirty)
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
        var document = Tabs.FirstOrDefault(tab =>
            string.Equals(tab.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (document is null || !File.Exists(fullPath))
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

    private static string? TryGetRelativePath(string? workspaceRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        }
        catch
        {
            return null;
        }
    }
}
