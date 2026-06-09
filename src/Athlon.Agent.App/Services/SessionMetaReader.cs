using System.Text.Json;

namespace Athlon.Agent.App.Services;

/// <summary>Reads display-only metadata from session.json without full deserialization.</summary>
internal static class SessionMetaReader
{
    public static int TryReadMessageCount(string sessionDirectory)
    {
        var sessionJsonPath = Path.Combine(sessionDirectory, "session.json");
        if (!File.Exists(sessionJsonPath))
        {
            return 0;
        }

        try
        {
            using var stream = File.OpenRead(sessionJsonPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                return messages.GetArrayLength();
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return 0;
    }
}
