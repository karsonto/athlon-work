using System.Text.Encodings.Web;
using System.Text.Json;

namespace Athlon.Agent.Core;

/// <summary>
/// Human-readable JSON formatting for tool results and UI display.
/// Preserves UTF-8 characters instead of emitting <c>\uXXXX</c> escapes.
/// </summary>
public static class JsonElementFormatter
{
    public static string FormatForDisplay(JsonElement element, bool indented = true) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => JsonSerializer.Serialize(element, Options(indented))
        };

    public static string SerializeForDisplay<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, Options(indented));

    public static string TryPrettyPrintJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0
            || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return text;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return JsonSerializer.Serialize(document.RootElement, Options(indented: true));
        }
        catch (JsonException)
        {
            return text;
        }
    }

    internal static JsonSerializerOptions Options(bool indented) =>
        indented ? JsonFileStoreOptions.WebIndented : JsonFileStoreOptions.WebCompactRelaxed;
}
