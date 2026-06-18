using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Athlon.Agent.Core.Knowledge;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class KnowledgeViewModel : ObservableObject
{
    private readonly IKnowledgeStore _store;
    private readonly IKnowledgeIndexer _indexer;
    private readonly IKnowledgeSearchService _searchService;
    private string _sessionId = "";
    private bool _suppressSelectionSave;
    private string? _activeSearchModuleId;
    private string? _activeSearchDocumentId;

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
    private string _statusText = "知识库未启用时仍可管理内容，但 Agent 不会暴露 knowledge_search 工具。";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _searchResults = "在左侧选择知识空间或具体文档后可测试检索。";

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private double _indexingProgress;

    [ObservableProperty]
    private string _indexingProgressText = "";

    public async Task SetSessionAsync(string sessionId)
    {
        _sessionId = sessionId;
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RefreshAsync(SelectedModule?.Module.Id);
    }

    private async Task RefreshAsync(string? preferredModuleId)
    {
        // #region agent log
        DebugLog("pre-fix", "H2,H3,H4", "KnowledgeViewModel.RefreshAsync:start", new
        {
            preferredModuleId,
            selectedModuleId = SelectedModule?.Module.Id,
            modulesBefore = Modules.Count,
            treeBefore = DocumentTree.Count
        });
        // #endregion
        await _store.InitializeAsync();
        var enabled = string.IsNullOrWhiteSpace(_sessionId)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await _store.GetSessionSelectionAsync(_sessionId);

        _suppressSelectionSave = true;
        Modules.Clear();
        DocumentTree.Clear();
        foreach (var summary in await _store.ListModulesAsync())
        {
            var moduleItem = new KnowledgeModuleItemViewModel(summary, enabled.Contains(summary.Module.Id), OnModuleSelectionChangedAsync);
            Modules.Add(moduleItem);
            var moduleNode = KnowledgeTreeNodeViewModel.ForModule(moduleItem);
            foreach (var document in await _store.ListDocumentsAsync(summary.Module.Id))
            {
                moduleNode.Children.Add(KnowledgeTreeNodeViewModel.ForDocument(new KnowledgeDocumentItemViewModel(document)));
            }

            DocumentTree.Add(moduleNode);
        }
        _suppressSelectionSave = false;

        SelectedModule = !string.IsNullOrWhiteSpace(preferredModuleId)
            ? Modules.FirstOrDefault(module => string.Equals(module.Module.Id, preferredModuleId, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedModule ??= Modules.FirstOrDefault();
        _activeSearchModuleId = SelectedModule?.Module.Id;
        _activeSearchDocumentId = null;
        await LoadDocumentsAsync(SelectedModule?.Module.Id);
        // #region agent log
        DebugLog("pre-fix", "H2,H3,H4", "KnowledgeViewModel.RefreshAsync:end", new
        {
            preferredModuleId,
            moduleCount = Modules.Count,
            treeRootCount = DocumentTree.Count,
            selectedModuleId = SelectedModule?.Module.Id,
            selectedModuleNameLength = SelectedModule?.Module.Name.Length,
            documentCount = Documents.Count,
            treeChildCounts = DocumentTree.Select(node => node.Children.Count).ToArray()
        });
        // #endregion
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

        await RefreshAsync(module.Id);
        StatusText = $"已创建知识空间「{module.Name}」。";
    }

    [RelayCommand]
    private async Task SaveSelectedModuleAsync()
    {
        // #region agent log
        DebugLog("pre-fix", "H1,H2", "KnowledgeViewModel.SaveSelectedModuleAsync:entry", new
        {
            hasSelectedModule = SelectedModule is not null,
            selectedModuleId = SelectedModule?.Module.Id,
            nameLength = ModuleName.Length,
            descriptionLength = ModuleDescription.Length
        });
        // #endregion

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
            // #region agent log
            DebugLog("pre-fix", "H2", "KnowledgeViewModel.SaveSelectedModuleAsync:after-store-save", new
            {
                moduleId = module.Id,
                savedId = saved.Id,
                savedNameLength = saved.Name.Length,
                savedDescriptionLength = saved.Description.Length
            });
            // #endregion
            await RefreshAsync(saved.Id);
            StatusText = $"知识空间已保存：{SelectedModule?.Module.Name ?? "未选择"}";
            // #region agent log
            DebugLog("pre-fix", "H3,H4", "KnowledgeViewModel.SaveSelectedModuleAsync:after-refresh", new
            {
                moduleId = saved.Id,
                statusTextLength = StatusText.Length,
                selectedModuleId = SelectedModule?.Module.Id,
                selectedModuleNameLength = SelectedModule?.Module.Name.Length,
                treeRootCount = DocumentTree.Count,
                treeNamesLengths = DocumentTree.Select(node => node.DisplayName.Length).ToArray()
            });
            // #endregion
        }
        catch (Exception exception)
        {
            // #region agent log
            DebugLog("pre-fix", "H2", "KnowledgeViewModel.SaveSelectedModuleAsync:error", new
            {
                exceptionType = exception.GetType().Name,
                exception.Message
            });
            // #endregion
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
        for (var index = 0; index < files.Length; index++)
        {
            var fileName = files[index];
            try
            {
                StatusText = $"正在索引 {Path.GetFileName(fileName)} ...";
                var progress = new Progress<KnowledgeIndexingProgress>(value =>
                    UpdateIndexingProgress(value, index, files.Length));
                await _indexer.ImportDocumentAsync(SelectedModule.Module.Id, fileName, progress: progress);
            }
            catch (Exception exception)
            {
                MessageBox.Show($"无法索引 {Path.GetFileName(fileName)}：{exception.Message}", "知识库索引失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        await RefreshAsync(SelectedModule.Module.Id);
        StatusText = "上传处理完成。";
        IndexingProgress = 100;
        IndexingProgressText = "索引完成";
        IsIndexing = false;
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
        await RefreshAsync();
        StatusText = $"已删除知识空间「{name}」。";
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
        await RefreshAsync(moduleId);
        StatusText = $"已删除文档「{fileName}」。";
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
            await RefreshAsync(SelectedModule?.Module.Id);
            StatusText = "重新索引完成。";
            IndexingProgress = 100;
            IndexingProgressText = "重新索引完成";
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
        _ = LoadDocumentsAsync(value?.Module.Id);
    }

    partial void OnSelectedDocumentChanged(KnowledgeDocumentItemViewModel? value)
    {
        DocumentPreview = ReadDocumentPreview(value?.Document);
    }

    private async Task LoadDocumentsAsync(string? moduleId)
    {
        Documents.Clear();
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            return;
        }

        foreach (var document in await _store.ListDocumentsAsync(moduleId))
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

    private async Task OnModuleSelectionChangedAsync()
    {
        if (_suppressSelectionSave || string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        var enabled = Modules
            .Where(module => module.IsEnabledForSession)
            .Select(module => module.Module.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _store.SaveSessionSelectionAsync(_sessionId, enabled);
        StatusText = enabled.Count == 0
            ? "当前会话未启用任何知识空间。"
            : $"当前会话已启用 {enabled.Count} 个知识空间。";
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

    private static void DebugLog(string runId, string hypothesisId, string message, object data)
    {
        try
        {
            var payload = new
            {
                sessionId = "6740f2",
                id = Guid.NewGuid().ToString("N"),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                location = "KnowledgeViewModel.cs",
                message,
                data,
                runId,
                hypothesisId
            };
            File.AppendAllText("F:/athlon-work/debug-6740f2.log", JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch
        {
            // Debug logging must never affect app behavior.
        }
    }
}

public sealed partial class KnowledgeModuleItemViewModel : ObservableObject
{
    private readonly Func<Task> _onSelectionChanged;

    public KnowledgeModuleItemViewModel(KnowledgeModuleSummary summary, bool enabledForSession, Func<Task> onSelectionChanged)
    {
        Module = summary.Module;
        DocumentCount = summary.DocumentCount;
        ChunkCount = summary.ChunkCount;
        _isEnabledForSession = enabledForSession;
        _onSelectionChanged = onSelectionChanged;
    }

    public KnowledgeModule Module { get; }
    public int DocumentCount { get; }
    public int ChunkCount { get; }
    public string MetaText => $"{DocumentCount} 个文档 · {ChunkCount} 个切片";

    [ObservableProperty]
    private bool _isEnabledForSession;

    partial void OnIsEnabledForSessionChanged(bool value)
    {
        _ = _onSelectionChanged();
    }
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
