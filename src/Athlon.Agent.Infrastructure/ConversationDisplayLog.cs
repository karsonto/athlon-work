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
                && TryGetRole(roleElement, out _)
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
            || !TryGetRole(roleElement, out var role))
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

    private static bool TryGetRole(JsonElement roleElement, out MessageRole role)
    {
        if (roleElement.ValueKind == JsonValueKind.String)
        {
            return Enum.TryParse(roleElement.GetString(), ignoreCase: true, out role);
        }

        if (roleElement.ValueKind == JsonValueKind.Number
            && roleElement.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(MessageRole), numeric))
        {
            role = (MessageRole)numeric;
            return true;
        }

        role = default;
        return false;
    }
}
