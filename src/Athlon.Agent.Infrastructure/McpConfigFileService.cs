using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public static class McpConfigFileService
{
    public const string FileName = "mcp.json";

    public static string GetPath(IAppPathProvider paths) => Path.Combine(paths.ConfigPath, FileName);

    public static List<McpServerSettings> LoadServers(IAppPathProvider paths) =>
        ParseServersJson(ReadServersJson(paths));

    public static async Task<List<McpServerSettings>> LoadServersAsync(IAppPathProvider paths, CancellationToken cancellationToken = default)
    {
        var path = GetPath(paths);
        if (!File.Exists(path))
        {
            return new List<McpServerSettings>();
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseServersJson(json);
    }

    private static string? ReadServersJson(IAppPathProvider paths)
    {
        var path = GetPath(paths);
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path);
    }

    private static List<McpServerSettings> ParseServersJson(string? json)
    {
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

        return JsonConfigFileService.Deserialize<List<McpServerSettings>>(json) ?? new List<McpServerSettings>();
    }

    public static async Task SaveServersAsync(IAppPathProvider paths, IEnumerable<McpServerSettings> servers, CancellationToken cancellationToken = default)
    {
        var path = GetPath(paths);
        var config = ClaudeDesktopMcpConfigMapper.FromSettingsList(servers);
        var json = ClaudeDesktopMcpConfigMapper.Serialize(config);
        Directory.CreateDirectory(paths.ConfigPath);
        await AtomicFile.WriteAllTextAsync(path, json, cancellationToken);
    }
}
