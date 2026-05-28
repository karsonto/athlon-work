using System.Text.Json;

namespace Athlon.Agent.Mcp;

public static class McpArgumentsJson
{
    public static IReadOnlyDictionary<string, object?>? ParseDictionary(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => ToObject(property.Value));
    }

    private static object? ToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => ToObject(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer)
                ? integer
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
}
