using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Reads session metadata from <c>session.json</c> without deserializing the messages array.
/// </summary>
internal static class SessionJsonIndexReader
{
    public static SessionIndexEntry? TryRead(string sessionJsonPath)
    {
        if (!File.Exists(sessionJsonPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(sessionJsonPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!TryGetString(root, "id", out var id) || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var title = TryGetString(root, "title", out var parsedTitle) ? parsedTitle ?? "New chat" : "New chat";
            var updatedAt = TryGetDateTimeOffset(root, "updatedAt", out var parsedUpdated)
                ? parsedUpdated
                : TryGetDateTimeOffset(root, "createdAt", out var parsedCreated)
                    ? parsedCreated
                    : File.GetLastWriteTimeUtc(sessionJsonPath);

            var sessionDir = Path.GetDirectoryName(sessionJsonPath)!;
            return new SessionIndexEntry(id, title, sessionDir, updatedAt);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryGetDateTimeOffset(JsonElement root, string propertyName, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        return element.TryGetDateTimeOffset(out value);
    }
}
