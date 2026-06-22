using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
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
    private string _sessionId = "";
    private string? _activeSearchModuleId;
    private string? _activeSearchDocumentId;
    private bool _isStale = true;
    private IReadOnlyDictionary<string, List<KnowledgeDocument>> _documentsByModuleId =
        new Dictionary<string, List<KnowledgeDocument>>(StringComparer.OrdinalIgnoreCase);

    public KnowledgeViewModel(
        IKnowledgeStore store,
        IKnowledgeIndexer indexer,
        IKnowledgeSearchService searchService)
    {
        _store = store;
        _indexer = indexer;
        _searchService = searchService;
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
    private string _documentPreview = "选择一个文档查看抽取文本预览。";

    [ObservableProperty]
    private string _statusText = "在聊天输入区开启知识库开关后，Agent 才会使用知识库检索来回答你的问题。";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _searchResults = "在左侧选择知识空间或文档后，输入问题可测试检索效果。";

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
            DocumentPreview = "选择一个文档查看抽取文本预览。";
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
            ? $"新知识空间 {index}"
            : ModuleName.Trim();

        var module = await _store.SaveModuleAsync(new KnowledgeModule
        {
            Name = name,
            Description = ModuleDescription.Trim()
        });

        InvalidateCache();
        await RefreshAsync(module.Id);
        StatusText = $"已创建知识空间「{module.Name}」。";
        KnowledgeDataChanged?.Invoke();;
    }

    [RelayCommand]
    private async Task SaveSelectedModuleAsync()
    {
        if (string.IsNullOrWhiteSpace(ModuleName))
        {
            MessageBox.Show("知识空间名称不能为空。", "知识库", MessageBoxButton.OK, MessageBoxImage.Information);
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
            StatusText = $"知识空间已保存：{SelectedModule?.Module.Name ?? "未选择"}";
            KnowledgeDataChanged?.Invoke();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"保存知识空间失败：{exception.Message}", "知识库", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText = $"保存知识空间失败：{exception.Message}";
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
            MessageBox.Show("请先选择或创建一个知识空间。", "知识库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "上传知识库文档",
            Multiselect = true,
            Filter = "知识库文档|*.txt;*.md;*.pdf;*.docx;*.csv;*.xlsx;*.pptx|所有文件|*.*"
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
            MessageBox.Show("请先选择或创建一个知识空间。", "知识库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = fileNames.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            return;
        }

        IsIndexing = true;
        IndexingProgress = 0;
        IndexingProgressText = "准备索引...";
        var succeeded = 0;
        var failed = 0;
        for (var index = 0; index < files.Length; index++)
        {
            var fileName = files[index];
            try
            {
                StatusText = $"正在索引 {Path.GetFileName(fileName)} ...";
                var progress = new Progress<KnowledgeIndexingProgress>(value =>
                    UpdateIndexingProgress(value, index, files.Length));
                await _indexer.ImportDocumentAsync(SelectedModule.Module.Id, fileName, progress: progress);
                succeeded++;
            }
            catch (Exception exception)
            {
                failed++;
                MessageBox.Show($"无法索引 {Path.GetFileName(fileName)}：{exception.Message}", "知识库索引失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        InvalidateCache();
        await RefreshAsync(SelectedModule.Module.Id);
        StatusText = failed == 0
            ? $"上传处理完成，共 {succeeded} 个文档。"
            : $"上传完成：成功 {succeeded} 个，失败 {failed} 个。";
        IndexingProgress = failed == 0 ? 100 : Math.Clamp(IndexingProgress, 0, 99);
        IndexingProgressText = failed == 0 ? "索引完成" : "部分文档索引失败";
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
        if (MessageBox.Show($"确定删除知识空间「{name}」及其全部文档和切片吗？", "删除知识空间", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _store.DeleteModuleAsync(module.Module.Id);
        SelectedModule = null;
        SelectedDocument = null;
        DocumentPreview = "选择一个文档查看抽取文本预览。";
        InvalidateCache();
        await RefreshAsync();
        StatusText = $"已删除知识空间「{name}」。";
        KnowledgeDataChanged?.Invoke();
    }

    private async Task DeleteDocumentAsync(KnowledgeDocumentItemViewModel document)
    {
        var fileName = document.Document.FileName;
        if (MessageBox.Show($"确定删除文档「{fileName}」吗？", "删除文档", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var moduleId = document.Document.ModuleId;
        await _store.DeleteDocumentAsync(document.Document.Id);
        SelectedDocument = null;
        DocumentPreview = "选择一个文档查看抽取文本预览。";
        InvalidateCache();
        await RefreshAsync(moduleId);
        StatusText = $"已删除文档「{fileName}」。";
        KnowledgeDataChanged?.Invoke();;
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
            IndexingProgressText = "准备重新索引...";
            StatusText = $"正在重新索引 {SelectedDocument.Document.FileName} ...";
            var progress = new Progress<KnowledgeIndexingProgress>(value =>
                UpdateIndexingProgress(value, fileIndex: 0, fileCount: 1));
            await _indexer.ReindexDocumentAsync(SelectedDocument.Document.Id, progress: progress);
            InvalidateCache();
            await RefreshAsync(SelectedModule?.Module.Id);
            StatusText = "重新索引完成。";
            IndexingProgress = 100;
            IndexingProgressText = "重新索引完成";
            KnowledgeDataChanged?.Invoke();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"重新索引失败：{exception.Message}", "知识库", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            SearchResults = "请输入检索问题。";
            return;
        }

        var moduleId = _activeSearchModuleId ?? SelectedModule?.Module.Id;
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            SearchResults = "请先在左侧选择一个知识空间或文档。";
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
            SearchResults = $"没有命中结果。检索范围：{scopeText}。";
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"检索范围：{scopeText}");
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
            return $"知识空间「{moduleName}」";
        }

        var documentName = SelectedDocument is not null
            && string.Equals(SelectedDocument.Document.Id, documentId, StringComparison.OrdinalIgnoreCase)
                ? SelectedDocument.Document.FileName
                : Documents.FirstOrDefault(document =>
                    string.Equals(document.Document.Id, documentId, StringComparison.OrdinalIgnoreCase))?.Document.FileName ?? documentId;
        return $"文档「{documentName}」";
    }

    private void UpdateIndexingProgress(KnowledgeIndexingProgress progress, int fileIndex, int fileCount)
    {
        fileCount = Math.Max(1, fileCount);
        var overallPercent = ((fileIndex + progress.Percent / 100d) / fileCount) * 100d;
        IndexingProgress = Math.Clamp(overallPercent, 0, 100);
        IndexingProgressText = fileCount == 1
            ? $"{progress.Stage}：{progress.Message}"
            : $"文件 {fileIndex + 1}/{fileCount} · {progress.Stage}：{progress.Message}";
        StatusText = progress.Message;
    }

    private static string ReadDocumentPreview(KnowledgeDocument? document)
    {
        if (document is null)
        {
            return "选择一个文档查看抽取文本预览。";
        }

        if (!File.Exists(document.ExtractedPath))
        {
            return string.IsNullOrWhiteSpace(document.LastError)
                ? $"文档状态：{document.Status}"
                : $"文档状态：{document.Status}\n错误：{document.LastError}";
        }

        var text = File.ReadAllText(document.ExtractedPath);
        return text.Length <= 5000 ? text : text[..5000] + "\n... (truncated)";
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
    public string MetaText => $"{DocumentCount} 个文档 · {ChunkCount} 个切片";
}

public sealed class KnowledgeDocumentItemViewModel(KnowledgeDocument document)
{
    public KnowledgeDocument Document { get; } = document;
    public string FileName => Document.FileName;
    public string StatusText => Document.Status.ToString();
    public string MetaText => $"{Document.ChunkCount} 个切片 · {Document.FileType} · {Document.UpdatedAt:yyyy-MM-dd HH:mm}";
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
    public string DeleteMenuHeader => IsModule ? "删除知识空间" : "删除文档";
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
