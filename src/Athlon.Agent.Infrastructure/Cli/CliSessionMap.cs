using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Cli;

namespace Athlon.Agent.Infrastructure.Cli;

public sealed class CliSessionMap(IAppPathProvider paths)
{
    private readonly object _gate = new();

    public string? Get(string cwd)
    {
        var key = CliPaths.NormalizeLocalPath(cwd);
        lock (_gate)
        {
            return LoadUnlocked().TryGetValue(key, out var sessionId) ? sessionId : null;
        }
    }

    public void Set(string cwd, string sessionId)
    {
        var key = CliPaths.NormalizeLocalPath(cwd);
        lock (_gate)
        {
            var map = LoadUnlocked();
            map[key] = sessionId;
            SaveUnlocked(map);
        }
    }

    private Dictionary<string, string> LoadUnlocked()
    {
        var path = CliPaths.GetSessionsMapPath(paths.RootPath);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonFileStoreOptions.Web);
            return loaded is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnlocked(Dictionary<string, string> map)
    {
        var directory = CliPaths.GetDirectory(paths.RootPath);
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(map, JsonFileStoreOptions.WebIndented);
        File.WriteAllText(CliPaths.GetSessionsMapPath(paths.RootPath), json);
    }
}
