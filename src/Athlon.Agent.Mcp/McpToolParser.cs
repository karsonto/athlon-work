using System.Text.Json;
using System.Text.Json.Nodes;

namespace Athlon.Agent.Mcp;

public static class McpToolParser
{
    public static IReadOnlyList<McpTool> ParseTools(JsonElement result)
    {
        if (!result.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<McpTool>();
        }

        var parsed = new List<McpTool>();
        foreach (var tool in toolsElement.EnumerateArray())
        {
            var toolName = tool.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;
            var description = tool.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                ? descEl.GetString() ?? string.Empty
                : string.Empty;
            var schemaJson = tool.TryGetProperty("inputSchema", out var schemaEl)
                ? schemaEl.GetRawText()
                : "{}";

            if (!string.IsNullOrWhiteSpace(toolName))
            {
                parsed.Add(new McpTool(toolName, description, schemaJson));
            }
        }

        return parsed;
    }

    public static JsonNode? ParseArgumentsNode(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }
}
