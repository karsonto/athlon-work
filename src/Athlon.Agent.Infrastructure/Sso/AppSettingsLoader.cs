using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

public static class AppSettingsLoader
{
    public static AppSettings Load()
    {
        var paths = new AppPathProvider();
        paths.EnsureCreated();
        var settingsPath = Path.Combine(paths.ConfigPath, "settings.json");
        if (!File.Exists(settingsPath))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(settingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonFileStore.Options) ?? new AppSettings();
    }
}
