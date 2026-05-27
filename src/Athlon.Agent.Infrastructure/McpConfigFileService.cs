using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public static class McpConfigFileService
{
    public const string FileName = "mcp.json";

    public static string GetPath(IAppPathProvider paths) => Path.Combine(paths.ConfigPath, FileName);

    public static async Task<List<McpServerSettings>> LoadServersAsync(IAppPathProvider paths, CancellationToken cancellationToken = default)
    {
        var path = GetPath(paths);
        if (!File.Exists(path))
        {
            return new List<McpServerSettings>();
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<McpServerSettings>();
        }

        // Claude Desktop format: { "mcpServers": { ... } }
        if (json.Contains("\"mcpServers\"", StringComparison.OrdinalIgnoreCase))
        {
            if (!ClaudeDesktopMcpConfigMapper.TryParse(json, out var config, out _))
            {
                return new List<McpServerSettings>();
            }

            return ClaudeDesktopMcpConfigMapper.ToSettingsList(config!);
        }

        // Legacy: array of McpServerSettings
        try
        {
            var legacy = JsonSerializer.Deserialize<List<McpServerSettings>>(json, JsonFileStore.Options);
            return legacy ?? new List<McpServerSettings>();
        }
        catch
        {
            return new List<McpServerSettings>();
        }
    }

    public static Task SaveServersAsync(IAppPathProvider paths, IEnumerable<McpServerSettings> servers, CancellationToken cancellationToken = default)
    {
        var path = GetPath(paths);
        Directory.CreateDirectory(paths.ConfigPath);
        var config = ClaudeDesktopMcpConfigMapper.FromSettingsList(servers);
        var json = ClaudeDesktopMcpConfigMapper.Serialize(config);
        return AtomicFile.WriteAllTextAsync(path, json, cancellationToken);
    }
}
