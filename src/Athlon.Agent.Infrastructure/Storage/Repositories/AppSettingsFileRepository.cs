using Athlon.Agent.Core;
using Athlon.Agent.Core.Storage;

namespace Athlon.Agent.Infrastructure.Storage.Repositories;

public sealed class AppSettingsFileRepository(IAppPathProvider paths, IJsonFileStore jsonFileStore) : IAppSettingsRepository
{
    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Model.LegacyApiKeyCredentialName = null;
        Directory.CreateDirectory(paths.ConfigPath);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "settings.json"), settings, cancellationToken);
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var path = Path.Combine(paths.ConfigPath, "settings.json");
        var settings = await jsonFileStore.LoadAsync<AppSettings>(path, cancellationToken);
        if (settings is null)
        {
            var defaults = CreateDefaultSettings();
            await SaveSettingsAsync(defaults, cancellationToken);
            return defaults;
        }

        if (RemoveLegacyMyDocumentsWorkspace(settings))
        {
            await SaveSettingsAsync(settings, cancellationToken);
        }

        return settings;
    }

    private static AppSettings CreateDefaultSettings() => new();

    private static bool RemoveLegacyMyDocumentsWorkspace(AppSettings settings)
    {
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var removed = settings.Workspaces.RemoveAll(workspace =>
            !string.IsNullOrWhiteSpace(workspace.RootPath)
            &&
            string.Equals(Path.GetFullPath(workspace.RootPath), Path.GetFullPath(myDocuments), StringComparison.OrdinalIgnoreCase));

        return removed > 0;
    }
}
