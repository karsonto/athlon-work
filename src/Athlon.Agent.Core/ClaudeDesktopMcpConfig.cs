using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athlon.Agent.Core;

/// <summary>
/// Claude Desktop–compatible MCP configuration: <c>{ "mcpServers": { "name": { ... } } }</c>.
/// </summary>
public sealed class ClaudeDesktopMcpConfig
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, ClaudeDesktopMcpServerEntry> McpServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClaudeDesktopMcpServerEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "stdio";

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Athlon extension: when true, server is kept in config but not started.</summary>
    [JsonPropertyName("disabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }
}

public static class ClaudeDesktopMcpConfigMapper
{
    public static List<McpServerSettings> ToSettingsList(ClaudeDesktopMcpConfig config)
    {
        var list = new List<McpServerSettings>();
        foreach (var (name, entry) in config.McpServers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            list.Add(new McpServerSettings
            {
                Name = name.Trim(),
                Enabled = !entry.Disabled,
                TransportType = string.IsNullOrWhiteSpace(entry.Type) ? "stdio" : entry.Type.Trim(),
                Url = entry.Url ?? string.Empty,
                Command = entry.Command ?? string.Empty,
                Args = entry.Args?.ToList() ?? new List<string>(),
                Env = entry.Env is null ? new Dictionary<string, string>() : new Dictionary<string, string>(entry.Env),
                Headers = entry.Headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(entry.Headers)
            });
        }

        return list;
    }

    public static ClaudeDesktopMcpConfig FromSettingsList(IEnumerable<McpServerSettings> servers)
    {
        var config = new ClaudeDesktopMcpConfig();
        foreach (var server in servers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                continue;
            }

            var entry = new ClaudeDesktopMcpServerEntry
            {
                Type = string.IsNullOrWhiteSpace(server.TransportType) ? "stdio" : server.TransportType,
                Url = server.Url ?? string.Empty,
                Command = server.Command ?? string.Empty,
                Args = server.Args?.ToList() ?? new List<string>(),
                Env = server.Env is null ? new Dictionary<string, string>() : new Dictionary<string, string>(server.Env),
                Headers = server.Headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(server.Headers),
                Disabled = !server.Enabled
            };

            config.McpServers[server.Name.Trim()] = entry;
        }

        return config;
    }

    public static bool TryParse(string json, out ClaudeDesktopMcpConfig? config, out string? error)
    {
        config = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty JSON.";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ClaudeDesktopMcpConfig>(json, JsonFileStoreOptions.Web);
            if (parsed is null)
            {
                error = "Invalid MCP config JSON.";
                return false;
            }

            config = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Serialize(ClaudeDesktopMcpConfig config) =>
        JsonSerializer.Serialize(config, JsonFileStoreOptions.WebIndented);
}

/// <summary>Shared JSON options for MCP config files (matches infrastructure store).</summary>
public static class JsonFileStoreOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions WebIndented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
