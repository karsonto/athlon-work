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

            return json.RootElement.EnumerateObject()
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Last().Value.ValueKind == JsonValueKind.String
                        ? g.Last().Value.GetString() ?? string.Empty
                        : g.Last().Value.GetRawText(),
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
