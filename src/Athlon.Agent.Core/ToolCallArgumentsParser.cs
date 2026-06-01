using System.Text.Json;

namespace Athlon.Agent.Core;

public static class ToolCallArgumentsParser
{
    public static IReadOnlyDictionary<string, string> ParseJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            using var json = JsonDocument.Parse(argumentsJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>();
            }

            return json.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText());
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
