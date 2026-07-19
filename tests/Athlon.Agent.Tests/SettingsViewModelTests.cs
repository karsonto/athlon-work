using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Changing_language_applies_culture_immediately_without_save()
    {
        AppCultureManager.SetCulture("en-US");
        var settings = new AppSettings { Ui = { Language = "en-US" } };
        var viewModel = new SettingsViewModel(
            settings,
            new TestMcpRegistry(),
            new EmptySkillCatalog(),
            new TestAppPathProvider(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            new RecordingCredentialStore(),
            new NoOpStorage(),
            new ApiKeySecretMigrationService(new RecordingCredentialStore()),
            new LocalizationService());

        viewModel.Language = "zh-CN";

        Assert.Equal("zh-CN", settings.Ui.Language);
        Assert.Equal("zh-CN", AppCultureManager.Current.Name);
        Assert.Equal("确定", Strings.Get("Common_OK"));
    }

    [Theory]
    [InlineData(true, true, "Model 与 Embedding API Key 已更新")]
    [InlineData(true, false, "Model API Key 已更新")]
    [InlineData(false, true, "Embedding API Key 已更新")]
    [InlineData(false, false, "Model API Key 未变更")]
    public void BuildSaveStatusMessage_describes_key_updates(bool modelKeySaved, bool embeddingKeySaved, string expectedFragment)
    {
        AppCultureManager.SetCulture("zh-CN");
        var message = SettingsViewModel.BuildSaveStatusMessage(modelKeySaved, embeddingKeySaved);
        Assert.Contains(expectedFragment, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSettingsAsync_persists_model_api_key_when_entered()
    {
        using var temp = new TempDirectoryScope("athlon-settings-save");
        var paths = new TestAppPathProvider(temp.Root);
        paths.EnsureCreated();
        var credentials = new RecordingCredentialStore();
        var storage = new FileStorageService(
            new NoOpLogger(),
            paths,
            new JsonFileStore(),
            new AgentRunContextAccessor());
        var settings = new AppSettings();
        var viewModel = new SettingsViewModel(
            settings,
            new TestMcpRegistry(),
            new EmptySkillCatalog(),
            paths,
            credentials,
            storage,
            new ApiKeySecretMigrationService(credentials),
            new LocalizationService())
        {
            ApiKey = "sk-or-v1-test-key"
        };

        AppCultureManager.SetCulture("zh-CN");
        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("sk-or-v1-test-key", credentials.GetSaved(ModelSettings.ApiKeySecretName));
        Assert.True(await credentials.HasSecretAsync(ModelSettings.ApiKeySecretName));
        Assert.Equal(string.Empty, viewModel.ApiKey);
        Assert.Contains("Model API Key 已更新", viewModel.SettingsStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSettingsAsync_persists_language_to_settings_file()
    {
        using var temp = new TempDirectoryScope("athlon-settings-language");
        var paths = new TestAppPathProvider(temp.Root);
        paths.EnsureCreated();
        var storage = new FileStorageService(
            new NoOpLogger(),
            paths,
            new JsonFileStore(),
            new AgentRunContextAccessor());
        var settings = new AppSettings();
        var viewModel = new SettingsViewModel(
            settings,
            new TestMcpRegistry(),
            new EmptySkillCatalog(),
            paths,
            new RecordingCredentialStore(),
            storage,
            new ApiKeySecretMigrationService(new RecordingCredentialStore()),
            new LocalizationService())
        {
            Language = "zh-CN"
        };

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        var json = await File.ReadAllTextAsync(Path.Combine(paths.ConfigPath, "settings.json"));
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, JsonFileStore.Options);
        Assert.NotNull(reloaded);
        Assert.Equal("zh-CN", reloaded!.Ui.Language);
    }

    [Fact]
    public async Task SaveSettingsAsync_syncs_password_box_before_persisting()
    {
        using var temp = new TempDirectoryScope("athlon-settings-sync");
        var paths = new TestAppPathProvider(temp.Root);
        paths.EnsureCreated();
        var credentials = new RecordingCredentialStore();
        var storage = new FileStorageService(
            new NoOpLogger(),
            paths,
            new JsonFileStore(),
            new AgentRunContextAccessor());
        var viewModel = new SettingsViewModel(
            new AppSettings(),
            new TestMcpRegistry(),
            new EmptySkillCatalog(),
            paths,
            credentials,
            storage,
            new ApiKeySecretMigrationService(credentials),
            new LocalizationService());
        viewModel.SyncPendingSecrets = () => viewModel.ApiKey = "sk-or-v1-from-password-box";

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("sk-or-v1-from-password-box", credentials.GetSaved(ModelSettings.ApiKeySecretName));
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[name] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(name, out var value) ? value : null);

        public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.ContainsKey(name));

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(name);
            return Task.CompletedTask;
        }

        public string? GetSaved(string name) =>
            _secrets.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class EmptySkillCatalog : Athlon.Agent.Skills.IAgentSkillCatalog
    {
        public IReadOnlyList<Athlon.Agent.Skills.AgentSkill> Skills { get; } = Array.Empty<Athlon.Agent.Skills.AgentSkill>();

        public Athlon.Agent.Skills.AgentSkill? GetSkill(string name) => null;

        public Athlon.Agent.Skills.AgentSkill? GetSkillById(string skillId) => null;

        public void Reload()
        {
        }
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated() => Directory.CreateDirectory(RootPath);

        public string ResolveSkillPath(string path) => path;
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values)
        {
        }

        public void Information(string messageTemplate, params object[] values)
        {
        }

        public void Warning(string messageTemplate, params object[] values)
        {
        }

        public void Error(Exception exception, string messageTemplate, params object[] values)
        {
        }

        public IAppLogger ForContext(string sourceContext) => this;
    }
}
