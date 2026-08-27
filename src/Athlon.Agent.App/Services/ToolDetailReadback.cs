using Athlon.Agent.Core;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Loads full tool output for UI expand: conversation.jsonl (unstripped) + optional evicted archive.
/// </summary>
internal static class ToolDetailReadback
{
    public const int MaxDisplayChars = 262_144;

    public static async Task<string?> LoadDisplayDetailAsync(
        IFileStorageService storage,
        string sessionId,
        string? messageId,
        string? toolCallId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        ChatMessage? message = null;
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            message = await storage.TryLoadConversationMessageAsync(sessionId, messageId, cancellationToken)
                .ConfigureAwait(false);
        }

        var resolvedToolCallId = toolCallId;
        string? content = message?.Content;
        string? argumentsText = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            ToolMessageDisplayParser.ParseToolContent(
                content,
                out var parsedToolCallId,
                out _,
                out _,
                out _,
                out _,
                out argumentsText,
                out _);
            if (string.IsNullOrWhiteSpace(resolvedToolCallId))
            {
                resolvedToolCallId = parsedToolCallId;
            }
        }

        string? detailBody = null;
        if (!string.IsNullOrWhiteSpace(content)
            && content.Contains("[Tool result evicted", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(resolvedToolCallId))
        {
            var evicted = await storage.TryReadEvictedToolResultAsync(sessionId, resolvedToolCallId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(evicted))
            {
                detailBody = evicted;
            }
        }

        if (detailBody is null && !string.IsNullOrWhiteSpace(content))
        {
            detailBody = ToolResultDisplayFormatter.FormatDetail(content);
            if (string.IsNullOrWhiteSpace(detailBody))
            {
                detailBody = content;
            }
        }
        else if (detailBody is null && !string.IsNullOrWhiteSpace(resolvedToolCallId))
        {
            var evicted = await storage.TryReadEvictedToolResultAsync(sessionId, resolvedToolCallId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(evicted))
            {
                detailBody = evicted;
            }
        }

        if (string.IsNullOrWhiteSpace(detailBody) && string.IsNullOrWhiteSpace(argumentsText))
        {
            return null;
        }

        var assembled = Assemble(argumentsText, detailBody);
        return ChatMessageViewModel.TruncateToolDetailForDisplay(assembled, MaxDisplayChars);
    }

    private static string Assemble(string? argumentsText, string? detailBody)
    {
        var hasArgs = !string.IsNullOrWhiteSpace(argumentsText);
        var hasDetail = !string.IsNullOrWhiteSpace(detailBody);
        if (hasArgs && hasDetail)
        {
            return "Arguments:\n" + argumentsText!.Trim() + "\n\nResult:\n" + detailBody!.Trim();
        }

        if (hasArgs)
        {
            return "Arguments:\n" + argumentsText!.Trim();
        }

        return detailBody!.Trim();
    }
}
