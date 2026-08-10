namespace Athlon.Agent.Core;

/// <summary>Formats persisted tool-result content for UI display.</summary>
public static class ToolResultDisplayFormatter
{
    public static string FormatDetail(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var body = ModelMessageBuilder.StripToolCallIdAndMetadata(content);
        var pretty = JsonElementFormatter.TryPrettyPrintJson(body);
        if (IsJsonLike(pretty))
        {
            return $"```json\n{pretty}\n```";
        }

        return pretty;
    }

    private static bool IsJsonLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }
}
