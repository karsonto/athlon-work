using System.Collections.ObjectModel;
using System.IO;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Audio;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IMcpRegistry _mcpRegistry;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IAppPathProvider _paths;
    private readonly ICredentialStore _credentialStore;
    private readonly IFileStorageService _storage;
    private readonly ApiKeySecretMigrationService _apiKeySecretMigration;
    private readonly ILocalizationService _loc;
    private readonly ITtsClient _ttsClient;
    private bool _disposed;

    public SettingsViewModel(
        AppSettings settings,
        IMcpRegistry mcpRegistry,
        IAgentSkillCatalog skillCatalog,
        IAppPathProvider paths,
        ICredentialStore credentialStore,
        IFileStorageService storage,
        ApiKeySecretMigrationService apiKeySecretMigration,
        ILocalizationService localization,
        ITtsClient ttsClient)
    {
        Settings = settings;
        _mcpRegistry = mcpRegistry;
        _skillCatalog = skillCatalog;
        _paths = paths;
        _credentialStore = credentialStore;
        _storage = storage;
        _apiKeySecretMigration = apiKeySecretMigration;
        _loc = localization;
        _ttsClient = ttsClient;
        Language = Settings.Ui.Language;
        TerminalShell = WorkspaceTerminalBootstrap.NormalizeShellPreference(Settings.Ui.TerminalShell);
        SettingsStatus = _loc["Settings_DefaultStatus"];
        AppCultureManager.CultureChanged += OnCultureChanged;
        foreach (var server in Settings.McpServers)
        {
            McpServers.Add(new McpServerItemViewModel(server, _mcpRegistry, OnMcpServerEnabledChanged));
        }

        SelectedMcpServer = McpServers.FirstOrDefault();
        SyncSkillsFromCatalog();
    }

    public event EventHandler? McpConfigurationChanged;
    public event EventHandler? SkillConfigurationChanged;
    public event EventHandler? SettingsSaved;
    public event EventHandler<bool>? EmbeddingApiKeyAvailabilityChanged;
    /// <summary>Raised when TTS settings change in a way the chat timeline must pick up.</summary>
    public event EventHandler? TtsConfigurationChanged;

    /// <summary>Set by <c>SettingsPageView</c> to flush PasswordBox values before save.</summary>
    public Action? SyncPendingSecrets { get; set; }

    [ObservableProperty]
    private string settingsStatus = string.Empty;

    [ObservableProperty]
    private string language = "zh-CN";

    [ObservableProperty]
    private string terminalShell = WorkspaceTerminalBootstrap.ShellCmd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApiKeyMask))]
    private string apiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApiKeyMask))]
    private bool hasStoredApiKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApiKeyMask))]
    private bool isApiKeyRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKnowledgeEmbeddingApiKeyMask))]
    private string knowledgeEmbeddingApiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKnowledgeEmbeddingApiKeyMask))]
    private bool hasStoredKnowledgeEmbeddingApiKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKnowledgeEmbeddingApiKeyMask))]
    private bool isKnowledgeEmbeddingApiKeyRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTtsApiKeyMask))]
    private string ttsApiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTtsApiKeyMask))]
    private bool hasStoredTtsApiKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTtsApiKeyMask))]
    private bool isTtsApiKeyRevealed;

    /// <summary>Connection probe status shown under the TTS card.</summary>
    [ObservableProperty]
    private string ttsTestStatus = string.Empty;

    /// <summary>Voices reported by the TTS service, feeding the editable voice combo box.</summary>
    public ObservableCollection<string> TtsVoices { get; } = new();

    public bool ShowApiKeyMask =>
        HasStoredApiKey && !IsApiKeyRevealed && string.IsNullOrWhiteSpace(ApiKey);

    public bool ShowKnowledgeEmbeddingApiKeyMask =>
        HasStoredKnowledgeEmbeddingApiKey
        && !IsKnowledgeEmbeddingApiKeyRevealed
        && string.IsNullOrWhiteSpace(KnowledgeEmbeddingApiKey);

    public bool ShowTtsApiKeyMask =>
        HasStoredTtsApiKey
        && !IsTtsApiKeyRevealed
        && string.IsNullOrWhiteSpace(TtsApiKey);

    public string McpConfigPath => SettingsConfigPath;

    public async Task InitializeAsync()
    {
        HasStoredApiKey = await _apiKeySecretMigration
            .EnsureCurrentApiKeySecretAsync(Settings)
            .ConfigureAwait(true);
        HasStoredKnowledgeEmbeddingApiKey = await _credentialStore
            .HasSecretAsync(KnowledgeEmbeddingSettings.ApiKeySecretName)
            .ConfigureAwait(true);
        HasStoredTtsApiKey = await _credentialStore
            .HasSecretAsync(TtsSettings.ApiKeySecretName)
            .ConfigureAwait(true);
        EmbeddingApiKeyAvailabilityChanged?.Invoke(this, HasStoredKnowledgeEmbeddingApiKey);
    }

    [RelayCommand]
    private async Task ToggleApiKeyRevealAsync()
    {
        if (IsApiKeyRevealed)
        {
            IsApiKeyRevealed = false;
            ApiKey = string.Empty;
            OnPropertyChanged(nameof(ApiKey));
            return;
        }

        if (string.IsNullOrWhiteSpace(ApiKey) && HasStoredApiKey)
        {
            var secret = await _credentialStore
                .GetSecretAsync(ModelSettings.ApiKeySecretName)
                .ConfigureAwait(true);
            ApiKey = secret ?? string.Empty;
            OnPropertyChanged(nameof(ApiKey));
        }

        IsApiKeyRevealed = true;
    }

    [RelayCommand]
    private async Task ToggleKnowledgeEmbeddingApiKeyRevealAsync()
    {
        if (IsKnowledgeEmbeddingApiKeyRevealed)
        {
            IsKnowledgeEmbeddingApiKeyRevealed = false;
            KnowledgeEmbeddingApiKey = string.Empty;
            OnPropertyChanged(nameof(KnowledgeEmbeddingApiKey));
            return;
        }

        if (string.IsNullOrWhiteSpace(KnowledgeEmbeddingApiKey) && HasStoredKnowledgeEmbeddingApiKey)
        {
            var secret = await _credentialStore
                .GetSecretAsync(KnowledgeEmbeddingSettings.ApiKeySecretName)
                .ConfigureAwait(true);
            KnowledgeEmbeddingApiKey = secret ?? string.Empty;
            OnPropertyChanged(nameof(KnowledgeEmbeddingApiKey));
        }

        IsKnowledgeEmbeddingApiKeyRevealed = true;
    }

    [RelayCommand]
    private async Task ToggleTtsApiKeyRevealAsync()
    {
        if (IsTtsApiKeyRevealed)
        {
            IsTtsApiKeyRevealed = false;
            TtsApiKey = string.Empty;
            OnPropertyChanged(nameof(TtsApiKey));
            return;
        }

        if (string.IsNullOrWhiteSpace(TtsApiKey) && HasStoredTtsApiKey)
        {
            var secret = await _credentialStore
                .GetSecretAsync(TtsSettings.ApiKeySecretName)
                .ConfigureAwait(true);
            TtsApiKey = secret ?? string.Empty;
            OnPropertyChanged(nameof(TtsApiKey));
        }

        IsTtsApiKeyRevealed = true;
    }

    /// <summary>
    /// Probes <c>{Endpoint}/health</c> and surfaces readiness plus the voice list, so the user can
    /// pick a valid voice instead of typing one blind.
    /// </summary>
    [RelayCommand]
    private async Task TestTtsConnectionAsync()
    {
        SyncPendingSecrets?.Invoke();
        TtsTestStatus = _loc["Settings_TtsTestRunning"];

        try
        {
            var result = await _ttsClient.ProbeAsync(CancellationToken.None).ConfigureAwait(true);

            TtsVoices.Clear();
            foreach (var voice in result.Voices)
            {
                TtsVoices.Add(voice);
            }

            TtsTestStatus = result.Voices.Count == 0
                ? Strings.Format("Settings_TtsTestOkNoVoices", result.SampleRate)
                : Strings.Format(
                    "Settings_TtsTestOk",
                    result.Model ?? "-",
                    result.SampleRate,
                    result.Voices.Count);
        }
        catch (Exception ex)
        {
            TtsTestStatus = Strings.Format("Settings_TtsTestFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        SyncPendingSecrets?.Invoke();

        var modelKeySaved = false;
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            await _credentialStore
                .SaveSecretAsync(ModelSettings.ApiKeySecretName, ApiKey.Trim())
                .ConfigureAwait(true);
            ApiKey = string.Empty;
            IsApiKeyRevealed = false;
            HasStoredApiKey = true;
            OnPropertyChanged(nameof(ApiKey));
            modelKeySaved = true;
        }

        var embeddingKeySaved = false;
        if (!string.IsNullOrWhiteSpace(KnowledgeEmbeddingApiKey))
        {
            await _credentialStore
                .SaveSecretAsync(KnowledgeEmbeddingSettings.ApiKeySecretName, KnowledgeEmbeddingApiKey.Trim())
                .ConfigureAwait(true);
            KnowledgeEmbeddingApiKey = string.Empty;
            IsKnowledgeEmbeddingApiKeyRevealed = false;
            HasStoredKnowledgeEmbeddingApiKey = true;
            OnPropertyChanged(nameof(KnowledgeEmbeddingApiKey));
            EmbeddingApiKeyAvailabilityChanged?.Invoke(this, true);
            embeddingKeySaved = true;
        }

        var ttsKeySaved = false;
        if (!string.IsNullOrWhiteSpace(TtsApiKey))
        {
            await _credentialStore
                .SaveSecretAsync(TtsSettings.ApiKeySecretName, TtsApiKey.Trim())
                .ConfigureAwait(true);
            TtsApiKey = string.Empty;
            IsTtsApiKeyRevealed = false;
            HasStoredTtsApiKey = true;
            OnPropertyChanged(nameof(TtsApiKey));
            ttsKeySaved = true;
        }

        Settings.Model.LegacyApiKeyCredentialName = null;
        PruneEmptyWorkspaces(Settings);
        SyncSkillsFromCatalog();
        Settings.Ui.Language = Language;
        Settings.Ui.TerminalShell = WorkspaceTerminalBootstrap.NormalizeShellPreference(TerminalShell);
        AppCultureManager.ApplyFromSettings(Settings.Ui);
        await _storage.SaveSettingsAsync(Settings).ConfigureAwait(true);
        SettingsStatus = BuildSaveStatusMessage(modelKeySaved, embeddingKeySaved, ttsKeySaved);
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        TtsConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string BuildSaveStatusMessage(
        bool modelKeySaved,
        bool embeddingKeySaved,
        bool ttsKeySaved = false)
    {
        var time = AppTimeZone.Now.ToString("HH:mm:ss");
        if (modelKeySaved && embeddingKeySaved)
        {
            return Strings.Format("Settings_SaveStatusBoth", time);
        }

        if (modelKeySaved)
        {
            return Strings.Format("Settings_SaveStatusModel", time);
        }

        if (embeddingKeySaved)
        {
            return Strings.Format("Settings_SaveStatusEmbedding", time);
        }

        if (ttsKeySaved)
        {
            return Strings.Format("Settings_SaveStatusTts", time);
        }

        return Strings.Format("Settings_SaveStatusNoChange", time);
    }

    private void OnMcpServerEnabledChanged() => McpConfigurationChanged?.Invoke(this, EventArgs.Empty);

    private void OnSkillEnabledChanged() => SkillConfigurationChanged?.Invoke(this, EventArgs.Empty);

    internal void RefreshRuntimeStates()
    {
        foreach (var server in McpServers)
        {
            server.RefreshRuntimeState();
        }
    }

    public void SyncSkillsFromCatalog()
    {
        _skillCatalog.Reload();
        var installed = _skillCatalog.Skills;
        var installedNames = installed.Select(skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = SkillSettingsMerger.Merge(_paths.SkillsPath, installed, Settings.Skills);
        Settings.Skills.Clear();
        Settings.Skills.AddRange(merged);

        Skills.Clear();
        foreach (var settings in merged.OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            var description = installed.FirstOrDefault(skill =>
                string.Equals(skill.Name, settings.Name, StringComparison.OrdinalIgnoreCase))?.Description
                ?? string.Empty;
            var isInstalled = installedNames.Contains(settings.Name);
            Skills.Add(new SkillItemViewModel(settings, description, isInstalled, OnSkillEnabledChanged));
        }
    }

    public AppSettings Settings { get; }
    public string SettingsConfigPath => Path.Combine(_paths.ConfigPath, "settings.json");
    public string SkillsDirectoryPath => _paths.SkillsPath;

    public IReadOnlyList<LanguageOption> LanguageOptions => AppCultureManager.GetLanguageOptions();

    public IReadOnlyList<TerminalShellOption> TerminalShellOptions =>
    [
        new(WorkspaceTerminalBootstrap.ShellCmd, _loc["Settings_TerminalShell_Cmd"]),
        new(WorkspaceTerminalBootstrap.ShellPowerShell, _loc["Settings_TerminalShell_PowerShell"]),
        new(WorkspaceTerminalBootstrap.ShellPwsh, _loc["Settings_TerminalShell_Pwsh"])
    ];

    partial void OnLanguageChanged(string value)
    {
        Settings.Ui.Language = value;
        AppCultureManager.ApplyFromSettings(Settings.Ui);
    }

    partial void OnTerminalShellChanged(string value)
    {
        Settings.Ui.TerminalShell = WorkspaceTerminalBootstrap.NormalizeShellPreference(value);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(TerminalShellOptions));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppCultureManager.CultureChanged -= OnCultureChanged;
    }


}
