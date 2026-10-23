namespace Athlon.Agent.Core.Storage;

public interface IAppSettingsRepository
{
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
}
