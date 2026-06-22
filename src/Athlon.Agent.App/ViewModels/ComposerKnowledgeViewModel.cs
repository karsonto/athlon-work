using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ComposerKnowledgeViewModel : ObservableObject
{
    private readonly ISessionKnowledgeState _sessionKnowledgeState;
    private readonly IKnowledgeStore _store;
    private readonly AppSettings _settings;
    private string _sessionId = "";
    private bool _suppressSave;
    private bool _hasStoredEmbeddingApiKey;

    public ComposerKnowledgeViewModel(
        ISessionKnowledgeState sessionKnowledgeState,
        IKnowledgeStore store,
        AppSettings settings)
    {
        _sessionKnowledgeState = sessionKnowledgeState;
        _store = store;
        _settings = settings;
        FilteredModulesView = CollectionViewSource.GetDefaultView(Modules);
        FilteredModulesView.Filter = FilterModules;
    }

    public ObservableCollection<ComposerKnowledgeModuleItemViewModel> Modules { get; } = new();
    public ObservableCollection<ComposerKnowledgeModuleItemViewModel> SelectedModules { get; } = new();
    public ICollectionView FilteredModulesView { get; }

    [ObservableProperty]
    private bool _isKnowledgeEnabledForChat;

    [ObservableProperty]
    private bool _isKnowledgePickerOpen;

    [ObservableProperty]
    private string _moduleSearchText = "";

    public bool IsKnowledgeToggleEnabled => IsEmbeddingConfigured();

    public bool ShowKnowledgePicker => IsKnowledgeEnabledForChat;

    public bool ShowKnowledgeChips => IsKnowledgeEnabledForChat && SelectedModules.Count > 0;

    public string KnowledgeToggleToolTip => IsEmbeddingConfigured()
        ? "为当前会话启用知识库检索工具"
        : "请先在设置页配置 Embedding Endpoint、Model 和 API Key";

    public string KnowledgePickerLabel => SelectedModuleCount == 0
        ? "选择知识空间"
        : $"知识库 · {SelectedModuleCount}";

    public int SelectedModuleCount => Modules.Count(module => module.IsSelected);

    public void SetEmbeddingApiKeyAvailable(bool hasStoredApiKey)
    {
        _hasStoredEmbeddingApiKey = hasStoredApiKey;
        OnPropertyChanged(nameof(IsKnowledgeToggleEnabled));
        OnPropertyChanged(nameof(KnowledgeToggleToolTip));
    }

    public async Task LoadForSessionAsync(string sessionId)
    {
        _sessionId = sessionId;
        await _store.InitializeAsync();
        await _sessionKnowledgeState.LoadAsync(sessionId);
        var snapshot = _sessionKnowledgeState.GetSnapshot(sessionId);

        _suppressSave = true;
        IsKnowledgeEnabledForChat = snapshot.Enabled;
        Modules.Clear();
        foreach (var summary in await _store.ListModulesAsync())
        {
            Modules.Add(new ComposerKnowledgeModuleItemViewModel(
                summary,
                snapshot.ModuleIds.Contains(summary.Module.Id),
                OnModuleSelectionChangedAsync));
        }

        _suppressSave = false;
        NotifyPickerStateChanged();
        UpdateSelectedModules();
        RefreshFilteredView();
    }

    partial void OnModuleSearchTextChanged(string value)
    {
        RefreshFilteredView();
    }

    private void RefreshFilteredView()
    {
        FilteredModulesView.Refresh();
    }

    private bool FilterModules(object o)
    {
        if (string.IsNullOrWhiteSpace(ModuleSearchText))
            return true;
        if (o is not ComposerKnowledgeModuleItemViewModel item)
            return false;
        return item.Name.Contains(ModuleSearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSelectedModules()
    {
        SelectedModules.Clear();
        foreach (var m in Modules.Where(m => m.IsSelected))
        {
            SelectedModules.Add(m);
        }
        OnPropertyChanged(nameof(ShowKnowledgeChips));
    }

    [RelayCommand]
    private void ToggleKnowledgePicker()
    {
        if (!IsKnowledgeEnabledForChat)
        {
            return;
        }

        IsKnowledgePickerOpen = !IsKnowledgePickerOpen;
    }

    partial void OnIsKnowledgeEnabledForChatChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _ = PersistStateAsync();
        OnPropertyChanged(nameof(ShowKnowledgePicker));
        OnPropertyChanged(nameof(ShowKnowledgeChips));
        if (!value)
        {
            IsKnowledgePickerOpen = false;
        }
    }

    private async Task OnModuleSelectionChangedAsync()
    {
        if (_suppressSave)
        {
            return;
        }

        await PersistStateAsync();
        UpdateSelectedModules();
    }

    private async Task PersistStateAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        var moduleIds = Modules
            .Where(module => module.IsSelected)
            .Select(module => module.ModuleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = new SessionKnowledgeSnapshot(IsKnowledgeEnabledForChat, moduleIds);
        await _sessionKnowledgeState.SaveAsync(_sessionId, snapshot);
        NotifyPickerStateChanged();
    }

    private void NotifyPickerStateChanged()
    {
        OnPropertyChanged(nameof(SelectedModuleCount));
        OnPropertyChanged(nameof(KnowledgePickerLabel));
        OnPropertyChanged(nameof(ShowKnowledgeChips));
    }

    private bool IsEmbeddingConfigured() =>
        !string.IsNullOrWhiteSpace(_settings.Knowledge.Embedding.Endpoint)
        && !string.IsNullOrWhiteSpace(_settings.Knowledge.Embedding.Model)
        && _hasStoredEmbeddingApiKey;
}

public sealed partial class ComposerKnowledgeModuleItemViewModel : ObservableObject
{
    private readonly Func<Task> _onSelectionChanged;

    public ComposerKnowledgeModuleItemViewModel(
        KnowledgeModuleSummary summary,
        bool isSelected,
        Func<Task> onSelectionChanged)
    {
        ModuleId = summary.Module.Id;
        Name = summary.Module.Name;
        MetaText = $"{summary.DocumentCount} 个文档 · {summary.ChunkCount} 个切片";
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public string ModuleId { get; }
    public string Name { get; }
    public string MetaText { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _ = _onSelectionChanged();
}
