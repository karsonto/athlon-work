using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athlon.Agent.Core;

/// <summary>
/// Reads <c>mcpServers</c> as either Athlon's array of <see cref="McpServerSettings"/>
/// or Claude Desktop's name→entry object map (and hybrid array entries).
/// Always writes Athlon array shape.
/// </summary>
public sealed class McpServersJsonConverter : JsonConverter<List<McpServerSettings>>
{
    public override List<McpServerSettings> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => new List<McpServerSettings>(),
            JsonTokenType.StartArray => ReadAthlonOrHybridArray(ref reader, options),
            JsonTokenType.StartObject => ReadClaudeDesktopObject(ref reader, options),
            _ => throw new JsonException(
                $"mcpServers must be a JSON array or object, got {reader.TokenType}.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<McpServerSettings> value,
        JsonSerializerOptions options)
    {
        // Avoid recursion through this converter when serializing list items.
        var itemOptions = CloneWithoutThisConverter(options);
        writer.WriteStartArray();
        foreach (var server in value)
        {
            JsonSerializer.Serialize(writer, server, itemOptions);
        }

        writer.WriteEndArray();
    }

    private static List<McpServerSettings> ReadClaudeDesktopObject(
        ref Utf8JsonReader reader,
        JsonSerializerOptions options)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, ClaudeDesktopMcpServerEntry>>(
            ref reader,
            options);
        if (map is null || map.Count == 0)
        {
            return new List<McpServerSettings>();
        }

        return ClaudeDesktopMcpConfigMapper.ToSettingsList(new ClaudeDesktopMcpConfig
        {
            McpServers = new Dictionary<string, ClaudeDesktopMcpServerEntry>(
                map,
                StringComparer.OrdinalIgnoreCase)
        });
    }

    private static List<McpServerSettings> ReadAthlonOrHybridArray(
        ref Utf8JsonReader reader,
        JsonSerializerOptions options)
    {
        var itemOptions = CloneWithoutThisConverter(options);
        var list = new List<McpServerSettings>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return list;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Unexpected token in mcpServers array: {reader.TokenType}.");
            }

            using var doc = JsonDocument.ParseValue(ref reader);
            var element = doc.RootElement;
            if (TryMapClaudeDesktopSingleton(element, options, out var fromDesktop))
            {
                list.Add(fromDesktop);
                continue;
            }

            var server = element.Deserialize<McpServerSettings>(itemOptions);
            if (server is not null)
            {
                list.Add(server);
            }
        }

        throw new JsonException("mcpServers array was not closed.");
    }

    /// <summary>
    /// Detects a mistaken paste of Claude Desktop shape inside the array:
    /// <c>{ "server-name": { "type": "...", "url": "..." } }</c>.
    /// </summary>
    private static bool TryMapClaudeDesktopSingleton(
        JsonElement element,
        JsonSerializerOptions options,
        out McpServerSettings server)
    {
        server = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Athlon entries always carry an explicit name (or other Athlon fields).
        if (element.TryGetProperty("name", out _)
            || element.TryGetProperty("Name", out _)
            || element.TryGetProperty("transportType", out _)
            || element.TryGetProperty("TransportType", out _)
            || element.TryGetProperty("command", out _)
            || element.TryGetProperty("Command", out _)
            || element.TryGetProperty("enabled", out _)
            || element.TryGetProperty("Enabled", out _))
        {
            return false;
        }

        string? serverName = null;
        JsonElement entryElement = default;
        var propertyCount = 0;
        foreach (var property in element.EnumerateObject())
        {
            propertyCount++;
            if (propertyCount > 1)
            {
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            serverName = property.Name;
            entryElement = property.Value;
        }

        if (propertyCount != 1 || string.IsNullOrWhiteSpace(serverName))
        {
            return false;
        }

        var entry = entryElement.Deserialize<ClaudeDesktopMcpServerEntry>(options);
        if (entry is null)
        {
            return false;
        }

        server = ClaudeDesktopMcpConfigMapper.ToSettingsList(new ClaudeDesktopMcpConfig
        {
            McpServers = new Dictionary<string, ClaudeDesktopMcpServerEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [serverName] = entry
            }
        })[0];
        return true;
    }

    private static JsonSerializerOptions CloneWithoutThisConverter(JsonSerializerOptions options)
    {
        var clone = new JsonSerializerOptions(options);
        for (var i = clone.Converters.Count - 1; i >= 0; i--)
        {
            if (clone.Converters[i] is McpServersJsonConverter)
            {
                clone.Converters.RemoveAt(i);
            }
        }

        return clone;
    }
}
