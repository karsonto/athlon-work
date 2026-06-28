using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core.Knowledge;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class KnowledgeViewModel : ObservableObject
{
    /// <summary>当知识空间/文档发生变更时触发，供 ComposerKnowledgeViewModel 等外部消费者刷新。</summary>
    public event Action? KnowledgeDataChanged;

    private readonly IKnowledgeStore _store;
    private readonly IKnowledgeIndexer _indexer;
    private readonly IKnowledgeSearchService _searchService;
    private readonly ILocalizationService _loc;
    private readonly IUserNotifier _notifier;
    private string _sessionId = "";
    private string? _activeSearchModuleId;
    private string? _activeSearchDocumentId;
    private bool _isStale = true;
    private IReadOnlyDictionary<string, List<KnowledgeDocument>> _documentsByModuleId =
        new Dictionary<string, List<KnowledgeDocument>>(StringComparer.OrdinalIgnoreCase);

    public KnowledgeViewModel(
        IKnowledgeStore store,
        IKnowledgeIndexer indexer,
        IKnowledgeSearchService searchService,
        ILocalizationService localization,
        IUserNotifier notifier)
    {
        _store = store;
        _indexer = indexer;
        _searchService = searchService;
        _loc = localization;
        _notifier = notifier;
        AppCultureManager.CultureChanged += OnCultureChanged;
        StatusText = _loc["Knowledge_DefaultStatus"];
        SearchResults = _loc["Knowledge_SearchResultsHint"];
        DocumentPreview = _loc["Knowledge_SelectDocumentPreview"];
        RefreshLocalizedStrings();
    }

    public ObservableCollection<KnowledgeModuleItemViewModel> Modules { get; } = new();
    public ObservableCollection<KnowledgeDocumentItemViewModel> Documents { get; } = new();
    public ObservableCollection<KnowledgeTreeNodeViewModel> DocumentTree { get; } = new();

    [ObservableProperty]
    private KnowledgeModuleItemViewModel? _selectedModule;

    [ObservableProperty]
    private KnowledgeDocumentItemViewModel? _selectedDocument;

    [ObservableProperty]
    private string _moduleName = "";

    [ObservableProperty]
    private string _moduleDescription = "";

    [ObservableProperty]
    private string _documentPreview = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _searchResults = "";

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private double _indexingProgress;

    [ObservableProperty]
    private string _indexingProgressText = "";

    public bool HasModuleSelected => SelectedModule is not null;

    public void SetSession(string sessionId)
    {
        _sessionId = sessionId;
    }

    public void InvalidateCache() => _isStale = true;

    public async Task RefreshIfStaleAsync()
    {
        if (!_isStale && Modules.Count > 0)
        {
            return;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RefreshAsync(SelectedModule?.Module.Id);
    }

    private async Task RefreshAsync(string? preferredModuleId)
    {
        IsLoading = true;
        try
        {
            await _store.InitializeAsync();
            var moduleSummaries = await _store.ListModulesAsync();
            var allDocuments = await _store.ListDocumentsAsync();
            _documentsByModuleId = allDocuments
                .GroupBy(document => document.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            Modules.Clear();
            DocumentTree.Clear();
            foreach (var summary in moduleSummaries)
            {
                var moduleItem = new KnowledgeModuleItemViewModel(summary);
                Modules.Add(moduleItem);
                var moduleNode = KnowledgeTreeNodeViewModel.ForModule(moduleItem);
                if (_documentsByModuleId.TryGetValue(summary.Module.Id, out var moduleDocuments))
                {
                    foreach (var document in moduleDocuments)
                    {
                        moduleNode.Children.Add(
                            KnowledgeTreeNodeViewModel.ForDocument(new KnowledgeDocumentItemViewModel(document)));
                    }
                }

                DocumentTree.Add(moduleNode);
            }

            SelectedModule = !string.IsNullOrWhiteSpace(preferredModuleId)
                ? Modules.FirstOrDefault(module => string.Equals(module.Module.Id, preferredModuleId, StringComparison.OrdinalIgnoreCase))
                : null;
            SelectedModule ??= Modules.FirstOrDefault();
            _activeSearchModuleId = SelectedModule?.Module.Id;
            _activeSearchDocumentId = null;
            LoadDocumentsFromCache(SelectedModule?.Module.Id);
            _isStale = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void SelectTreeNode(KnowledgeTreeNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        if (node.Module is not null)
        {
            SelectedModule = node.Module;
            SelectedDocument = null;
            _activeSearchModuleId = node.Module.Module.Id;
            _activeSearchDocumentId = null;
            DocumentPreview = _loc["Knowledge_SelectDocumentPreview"];
            return;
        }

        if (node.Document is not null)
        {
            SelectedModule = Modules.FirstOrDefault(module =>
                string.Equals(module.Module.Id, node.Document.Document.ModuleId, StringComparison.OrdinalIgnoreCase));
            SelectedDocument = node.Document;
            _activeSearchModuleId = node.Document.Document.ModuleId;
            _activeSearchDocumentId = node.Document.Document.Id;
        }
    }

    [RelayCommand]
    private async Task AddModuleAsync()
    {
        var index = Modules.Count + 1;
        var name = string.IsNullOrWhiteSpace(ModuleName)
            ? _loc.Format("Knowledge_NewModuleDefaultName", index)
            : ModuleName.Trim();

        var module = await _store.SaveModuleAsync(new KnowledgeModule
        {
            Name = name,
            Description = ModuleDescription.Trim()
        });

        InvalidateCache();
        await RefreshAsync(module.Id);
        StatusText = _loc.Format("Knowledge_ModuleCreated", module.Name);
        KnowledgeDataChanged?.Invoke();
    }

    [RelayCommand]
    private async Task SaveSelectedModuleAsync()
    {
        if (string.IsNullOrWhiteSpace(ModuleName))
        {
            _notifier.Info("Knowledge_TitleDialog", "Knowledge_ModuleNameRequired");
            return;
        }

        try
        {
            var module = SelectedModule?.Module ?? new KnowledgeModule();
            module.Name = ModuleName.Trim();
            module.Description = ModuleDescription.Trim();
            var saved = await _store.SaveModuleAsync(module);
            InvalidateCache();
            await RefreshAsync(saved.Id);
            StatusText = _loc.Format("Knowledge_ModuleSaved", SelectedModule?.Module.Name ?? _loc["Knowledge_ModuleNotSelected"]);
            KnowledgeDataChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _notifier.Warning("Knowledge_TitleDialog", "Knowledge_ModuleSaveFailed", exception.Message);
            StatusText = _loc.Format("Knowledge_ModuleSaveFailed", exception.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedModuleAsync()
    {
        if (SelectedModule is null)
        {
            return;
        }

        await DeleteModuleAsync(SelectedModule);
    }

    [RelayCommand]
    private async Task UploadDocumentsAsync()
    {
        if (SelectedModule is null)
        {
            _notifier.Info("Knowledge_TitleDialog", "Knowledge_SelectModuleFirst");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = _loc["Knowledge_UploadDialogTitle"],
            Multiselect = true,
            Filter = _loc["Knowledge_UploadFilter"]
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportDocumentsAsync(dialog.FileNames);
    }

    public async Task ImportDocumentsAsync(IEnumerable<string> fileNames)
    {
        if (SelectedModule is null)
        {
            _notifier.Info("Knowledge_TitleDialog", "Knowledge_SelectModuleFirst");
            return;
        }

        var files = fileNames.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            return;
        }

        IsIndexing = true;
        IndexingProgress = 0;
        IndexingProgressText = _loc["Knowledge_IndexingPrepare"];
        var succeeded = 0;
        var failed = 0;
        for (var index = 0; index < files.Length; index++)
        {
            var fileName = files[index];
            try
            {
                StatusText = _loc.Format("Knowledge_IndexingFile", Path.GetFileName(fileName));
                var progress = new Progress<KnowledgeIndexingProgress>(value =>
                    UpdateIndexingProgress(value, index, files.Length));
                await _indexer.ImportDocumentAsync(SelectedModule.Module.Id, fileName, progress: progress);
                succeeded++;
            }
            catch (Exception exception)
            {
                failed++;
                _notifier.Warning("Knowledge_IndexFailedTitle", "Knowledge_IndexFailedMessage", Path.GetFileName(fileName), exception.Message);
            }
        }

        InvalidateCache();
        await RefreshAsync(SelectedModule.Module.Id);
        StatusText = failed == 0
            ? _loc.Format("Knowledge_UploadAllSucceeded", succeeded)
            : _loc.Format("Knowledge_UploadPartial", succeeded, failed);
        IndexingProgress = failed == 0 ? 100 : Math.Clamp(IndexingProgress, 0, 99);
        IndexingProgressText = failed == 0 ? _loc["Knowledge_IndexComplete"] : _loc["Knowledge_IndexPartialFailed"];
        IsIndexing = false;
        KnowledgeDataChanged?.Invoke();
    }

    [RelayCommand]
    private async Task DeleteSelectedDocumentAsync()
    {
        if (SelectedDocument is null)
        {
            return;
        }

        await DeleteDocumentAsync(SelectedDocument);
    }

    [RelayCommand]
    private async Task DeleteTreeNodeAsync(KnowledgeTreeNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        SelectTreeNode(node);

        if (node.Module is not null)
        {
            await DeleteModuleAsync(node.Module);
            return;
        }

        if (node.Document is not null)
        {
            await DeleteDocumentAsync(node.Document);
        }
    }

    private async Task DeleteModuleAsync(KnowledgeModuleItemViewModel module)
    {
        var name = module.Module.Name;
        if (!_notifier.ConfirmYesNo("Knowledge_DeleteModuleTitle", "Knowledge_DeleteModuleMessage", name))
        {
            return;
        }

        await _store.DeleteModuleAsync(module.Module.Id);
        SelectedModule = null;
        SelectedDocument = null;
        DocumentPreview = _loc["Knowledge_SelectDocumentPreview"];
        InvalidateCache();
        await RefreshAsync();
        StatusText = _loc.Format("Knowledge_ModuleDeleted", name);
        KnowledgeDataChanged?.Invoke();
    }

    private async Task DeleteDocumentAsync(KnowledgeDocumentItemViewModel document)
    {
        var fileName = document.Document.FileName;
        if (!_notifier.ConfirmYesNo("Knowledge_DeleteDocumentTitle", "Knowledge_DeleteDocumentMessage", fileName))
        {
            return;
        }

        var moduleId = document.Document.ModuleId;
        await _store.DeleteDocumentAsync(document.Document.Id);
        SelectedDocument = null;
        DocumentPreview = _loc["Knowledge_SelectDocumentPreview"];
        InvalidateCache();
        await RefreshAsync(moduleId);
        StatusText = _loc.Format("Knowledge_DocumentDeleted", fileName);
        KnowledgeDataChanged?.Invoke();
    }

    [RelayCommand]
    private async Task ReindexSelectedDocumentAsync()
    {
        if (SelectedDocument is null)
        {
            return;
        }

        try
        {
            IsIndexing = true;
            IndexingProgress = 0;
            IndexingProgressText = _loc["Knowledge_ReindexPrepare"];
            StatusText = _loc.Format("Knowledge_Reindexing", SelectedDocument.Document.FileName);
            var progress = new Progress<KnowledgeIndexingProgress>(value =>
                UpdateIndexingProgress(value, fileIndex: 0, fileCount: 1));
            await _indexer.ReindexDocumentAsync(SelectedDocument.Document.Id, progress: progress);
            InvalidateCache();
            await RefreshAsync(SelectedModule?.Module.Id);
            StatusText = _loc["Knowledge_ReindexDone"];
            IndexingProgress = 100;
            IndexingProgressText = _loc["Knowledge_ReindexComplete"];
            KnowledgeDataChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _notifier.Warning("Knowledge_TitleDialog", "Knowledge_ReindexFailed", exception.Message);
        }
        finally
        {
            IsIndexing = false;
        }
    }

    [RelayCommand]
    private async Task TestSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchResults = _loc["Knowledge_SearchQueryRequired"];
            return;
        }

        var moduleId = _activeSearchModuleId ?? SelectedModule?.Module.Id;
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            SearchResults = _loc["Knowledge_SearchScopeRequired"];
            return;
        }

        var documentId = _activeSearchDocumentId;
        var scopeText = ResolveSearchScopeText(moduleId, documentId);
        var hits = await _searchService.SearchInScopeAsync(
            SearchQuery,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { moduleId },
            documentId);
        if (hits.Count == 0)
        {
            SearchResults = _loc.Format("Knowledge_NoSearchHits", scopeText);
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(_loc.Format("Knowledge_SearchScopeLine", scopeText));
        builder.AppendLine();
        foreach (var hit in hits)
        {
            builder.AppendLine($"score={hit.Score:0.000} | {hit.ModuleName} / {hit.FileName} / {hit.TitlePath}");
            builder.AppendLine(hit.Content.Trim());
            builder.AppendLine();
        }

        SearchResults = builder.ToString().TrimEnd();
    }

    partial void OnSelectedModuleChanged(KnowledgeModuleItemViewModel? value)
    {
        ModuleName = value?.Module.Name ?? "";
        ModuleDescription = value?.Module.Description ?? "";
        LoadDocumentsFromCache(value?.Module.Id);
        OnPropertyChanged(nameof(HasModuleSelected));
    }

    partial void OnSelectedDocumentChanged(KnowledgeDocumentItemViewModel? value)
    {
        DocumentPreview = ReadDocumentPreview(value?.Document);
    }

    private void LoadDocumentsFromCache(string? moduleId)
    {
        Documents.Clear();
        if (string.IsNullOrWhiteSpace(moduleId)
            || !_documentsByModuleId.TryGetValue(moduleId, out var moduleDocuments))
        {
            return;
        }

        foreach (var document in moduleDocuments)
        {
            Documents.Add(new KnowledgeDocumentItemViewModel(document));
        }

        var selectedDocument = SelectedDocument?.Document;
        SelectedDocument = selectedDocument is not null && string.Equals(selectedDocument.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)
            ? Documents.FirstOrDefault(document => string.Equals(document.Document.Id, selectedDocument.Id, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private string ResolveSearchScopeText(string moduleId, string? documentId)
    {
        var moduleName = Modules.FirstOrDefault(module =>
            string.Equals(module.Module.Id, moduleId, StringComparison.OrdinalIgnoreCase))?.Module.Name ?? moduleId;
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return _loc.Format("Knowledge_ScopeModule", moduleName);
        }

        var documentName = SelectedDocument is not null
            && string.Equals(SelectedDocument.Document.Id, documentId, StringComparison.OrdinalIgnoreCase)
                ? SelectedDocument.Document.FileName
                : Documents.FirstOrDefault(document =>
                    string.Equals(document.Document.Id, documentId, StringComparison.OrdinalIgnoreCase))?.Document.FileName ?? documentId;
        return _loc.Format("Knowledge_ScopeDocument", documentName);
    }

    private void UpdateIndexingProgress(KnowledgeIndexingProgress progress, int fileIndex, int fileCount)
    {
        fileCount = Math.Max(1, fileCount);
        var overallPercent = ((fileIndex + progress.Percent / 100d) / fileCount) * 100d;
        IndexingProgress = Math.Clamp(overallPercent, 0, 100);
        IndexingProgressText = fileCount == 1
            ? $"{progress.Stage}：{progress.Message}"
            : _loc.Format("Knowledge_IndexProgress", fileIndex + 1, fileCount, progress.Stage, progress.Message);
        StatusText = progress.Message;
    }

    private string ReadDocumentPreview(KnowledgeDocument? document)
    {
        if (document is null)
        {
            return _loc["Knowledge_SelectDocumentPreview"];
        }

        if (!File.Exists(document.ExtractedPath))
        {
            return string.IsNullOrWhiteSpace(document.LastError)
                ? _loc.Format("Knowledge_DocumentStatus", document.Status)
                : _loc.Format("Knowledge_DocumentStatusWithError", document.Status, document.LastError);
        }

        var text = File.ReadAllText(document.ExtractedPath);
        return text.Length <= 5000 ? text : text[..5000] + "\n... (truncated)";
    }

    private void OnCultureChanged(object? sender, EventArgs e) => RefreshLocalizedStrings();

    private void RefreshLocalizedStrings()
    {
        if (SelectedDocument is null)
        {
            DocumentPreview = _loc["Knowledge_SelectDocumentPreview"];
        }
        else
        {
            DocumentPreview = ReadDocumentPreview(SelectedDocument.Document);
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchResults = _loc["Knowledge_SearchResultsHint"];
        }

        OnPropertyChanged(nameof(Modules));
        OnPropertyChanged(nameof(DocumentTree));
    }
}

public sealed class KnowledgeModuleItemViewModel
{
    public KnowledgeModuleItemViewModel(KnowledgeModuleSummary summary)
    {
        Module = summary.Module;
        DocumentCount = summary.DocumentCount;
        ChunkCount = summary.ChunkCount;
    }

    public KnowledgeModule Module { get; }
    public int DocumentCount { get; }
    public int ChunkCount { get; }
    public string MetaText => Strings.Format("Knowledge_ModuleMeta", DocumentCount, ChunkCount);
}

public sealed class KnowledgeDocumentItemViewModel(KnowledgeDocument document)
{
    public KnowledgeDocument Document { get; } = document;
    public string FileName => Document.FileName;
    public string StatusText => Document.Status.ToString();
    public string MetaText => Strings.Format(
        "Knowledge_DocumentMeta",
        Document.ChunkCount,
        Document.FileType,
        Document.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));
}

public sealed class KnowledgeTreeNodeViewModel
{
    private KnowledgeTreeNodeViewModel(string name, string metaText, bool isModule)
    {
        DisplayName = name;
        MetaText = metaText;
        IsModule = isModule;
    }

    public string DisplayName { get; }
    public string MetaText { get; }
    public bool IsModule { get; }
    public string Glyph => IsModule ? "📁" : "📄";
    public string DeleteMenuHeader => IsModule
        ? Strings.Get("Knowledge_DeleteModuleMenu")
        : Strings.Get("Knowledge_DeleteDocumentMenu");
    public KnowledgeModuleItemViewModel? Module { get; private init; }
    public KnowledgeDocumentItemViewModel? Document { get; private init; }
    public ObservableCollection<KnowledgeTreeNodeViewModel> Children { get; } = new();

    public static KnowledgeTreeNodeViewModel ForModule(KnowledgeModuleItemViewModel module) =>
        new(module.Module.Name, module.MetaText, isModule: true)
        {
            Module = module
        };

    public static KnowledgeTreeNodeViewModel ForDocument(KnowledgeDocumentItemViewModel document) =>
        new(document.FileName, document.MetaText, isModule: false)
        {
            Document = document
        };
}
