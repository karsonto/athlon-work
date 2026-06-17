using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class ConversationDisplayLog
{
    public static ChatMessage? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("role", out var roleElement)
                && roleElement.ValueKind == JsonValueKind.String
                && Enum.TryParse<MessageRole>(roleElement.GetString(), ignoreCase: true, out var role)
                && root.TryGetProperty("createdAt", out _))
            {
                return JsonSerializer.Deserialize<ChatMessage>(line, JsonFileStore.JsonLineOptions);
            }

            return ParseLegacyLine(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatMessage? ParseLegacyLine(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (!root.TryGetProperty("role", out var roleElement)
            || roleElement.ValueKind != JsonValueKind.String
            || !Enum.TryParse<MessageRole>(roleElement.GetString(), ignoreCase: true, out var role))
        {
            return null;
        }

        var content = root.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        string? toolCallsJson = null;
        if (root.TryGetProperty("toolCalls", out var toolCallsElement)
            && toolCallsElement.ValueKind == JsonValueKind.String)
        {
            toolCallsJson = toolCallsElement.GetString();
        }

        string? reasoningContent = null;
        if (root.TryGetProperty("reasoningContent", out var reasoningElement)
            && reasoningElement.ValueKind == JsonValueKind.String)
        {
            reasoningContent = reasoningElement.GetString();
        }

        var createdAt = root.TryGetProperty("time", out var timeElement) && timeElement.TryGetDateTimeOffset(out var parsedTime)
            ? parsedTime
            : DateTimeOffset.UtcNow;

        string? parentId = null;
        if (root.TryGetProperty("parentId", out var parentElement)
            && parentElement.ValueKind == JsonValueKind.String)
        {
            parentId = parentElement.GetString();
        }

        IReadOnlyList<ImageAttachment>? imageAttachments = null;
        if (root.TryGetProperty("imageAttachments", out var attachmentsElement)
            && attachmentsElement.ValueKind == JsonValueKind.Array)
        {
            imageAttachments = JsonSerializer.Deserialize<IReadOnlyList<ImageAttachment>>(
                attachmentsElement.GetRawText(),
                JsonFileStore.JsonLineOptions);
        }

        return new ChatMessage(
            id,
            role,
            content,
            createdAt,
            parentId,
            toolCallsJson,
            reasoningContent,
            imageAttachments);
    }
}
