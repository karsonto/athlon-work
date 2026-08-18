namespace Athlon.Agent.Core;

internal static class ModelMessageBuilder
{
    /// <summary>Default matching <see cref="Compaction.ContextCompactionSettings.MaxToolScreenshotsInModelContext"/>.</summary>
    internal const int DefaultMaxToolScreenshotsInModelContext = 2;
    internal const string ToolScreenshotCaption =
        "[Computer Use screenshot returned by the preceding tool result.]";

    public static List<AgentModelMessage> BuildForSession(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext) =>
        BuildModelMessages(environmentPrompt, history, includeReasoningInModelContext);

    public static List<AgentModelMessage> BuildModelMessages(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext = false)
    {
        var messages = new List<AgentModelMessage>
        {
            new("system", environmentPrompt)
        };

        AppendHistoryRange(messages, history, 0, includeReasoningInModelContext);
        return messages;
    }

    /// <summary>
    /// Keeps at most <paramref name="maxImages"/> Computer Use tool screenshots in the API
    /// payload (newest first). Older tool-screenshot user messages are removed; user-uploaded
    /// images are left untouched. Values below 0 are treated as 0.
    /// </summary>
    public static void RetainLatestToolScreenshots(
        List<AgentModelMessage> messages,
        int maxImages = DefaultMaxToolScreenshotsInModelContext)
    {
        if (messages.Count == 0)
        {
            return;
        }

        maxImages = Math.Max(0, maxImages);
        var keptImages = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (!TryGetToolScreenshotParts(message, out var parts))
            {
                continue;
            }

            var imageIndexes = new List<int>();
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                if (IsImageUrlPart(parts[partIndex]))
                {
                    imageIndexes.Add(partIndex);
                }
            }

            if (imageIndexes.Count == 0)
            {
                continue;
            }

            if (keptImages >= maxImages)
            {
                messages.RemoveAt(index);
                continue;
            }

            var remaining = maxImages - keptImages;
            if (imageIndexes.Count <= remaining)
            {
                keptImages += imageIndexes.Count;
                continue;
            }

            // Keep the newest images within this message (last image_url parts).
            var dropCount = imageIndexes.Count - remaining;
            for (var drop = 0; drop < dropCount; drop++)
            {
                parts.RemoveAt(imageIndexes[drop]);
            }

            // Re-resolve after removals: text + remaining images.
            if (CountImageUrlParts(parts) == 0)
            {
                messages.RemoveAt(index);
                continue;
            }

            messages[index] = message with { Content = parts };
            keptImages += remaining;
        }
    }

    private static bool TryGetToolScreenshotParts(
        AgentModelMessage message,
        out List<object> parts)
    {
        parts = null!;
        if (!string.Equals(message.Role, "user", StringComparison.Ordinal))
        {
            return false;
        }

        if (message.Content is not IEnumerable<object> contentParts)
        {
            return false;
        }

        var list = contentParts as List<object> ?? contentParts.ToList();
        if (list.Count == 0 || !IsTextPart(list[0], ToolScreenshotCaption))
        {
            return false;
        }

        parts = list;
        return true;
    }

    private static bool IsTextPart(object part, string expectedText)
    {
        if (part is IDictionary<string, object?> map)
        {
            return map.TryGetValue("type", out var typeObj)
                && typeObj is string type
                && string.Equals(type, "text", StringComparison.Ordinal)
                && map.TryGetValue("text", out var textObj)
                && textObj is string text
                && string.Equals(text, expectedText, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsImageUrlPart(object part)
    {
        if (part is IDictionary<string, object?> map
            && map.TryGetValue("type", out var typeObj)
            && typeObj is string type)
        {
            return string.Equals(type, "image_url", StringComparison.Ordinal);
        }

        return false;
    }

    private static int CountImageUrlParts(IReadOnlyList<object> parts)
    {
        var count = 0;
        foreach (var part in parts)
        {
            if (IsImageUrlPart(part))
            {
                count++;
            }
        }

        return count;
    }

    public static int AppendHistoryMessage(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int index,
        bool includeReasoningInModelContext) =>
        AppendHistoryMessageCore(messages, history, index, includeReasoningInModelContext);

    private static void AppendHistoryRange(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int startIndex,
        bool includeReasoningInModelContext)
    {
        for (var index = startIndex; index < history.Count; index++)
        {
            index = AppendHistoryMessageCore(messages, history, index, includeReasoningInModelContext);
        }
    }

    private static int AppendHistoryMessageCore(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int index,
        bool includeReasoningInModelContext)
    {
        var message = history[index];
        switch (message.Role)
        {
            case MessageRole.Compaction:
                return index;
            case MessageRole.User:
                messages.Add(new AgentModelMessage("user", BuildUserContent(message)));
                return index;
            case MessageRole.Assistant:
                return AppendAssistantModelMessages(messages, history, index, includeReasoningInModelContext);
            case MessageRole.Tool:
            {
                var toolCallId = ExtractToolCallId(message.Content);
                if (toolCallId is not null)
                {
                    var stripped = StripToolCallIdAndMetadata(message.Content);
                    messages.Add(new AgentModelMessage("tool", stripped, toolCallId));
                    AppendToolImageMessage(messages, message);
                }
                else
                {
                    messages.Add(new AgentModelMessage("user", FormatToolResultAsUserContent(message.Content)));
                    AppendToolImageMessage(messages, message);
                }
                return index;
            }
            case MessageRole.Summary:
                messages.Add(new AgentModelMessage("user", $"History summary: {message.Content}"));
                return index;
            case MessageRole.System:
                messages.Add(new AgentModelMessage("user", message.Content));
                return index;
            default:
                messages.Add(new AgentModelMessage("user", message.Content));
                return index;
        }
    }

    public static string FormatToolResult(AgentToolCall call, ToolResult result)
    {
        var status = result.Succeeded ? "succeeded" : "failed";
        return string.Join(Environment.NewLine, new[]
        {
            $"ToolCallId: {call.Id}",
            $"Tool `{call.Name}` {status}.",
            "",
            $"Arguments: {FormatArguments(call)}",
            $"Summary: {result.Summary}",
            "",
            result.Content ?? result.Error ?? string.Empty
        });
    }

    public static string? ExtractToolCallId(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            const string prefix = "ToolCallId:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>Strip the metadata header (ToolCallId / status / arguments / summary) from a tool result,
    /// keeping only the actual output content.</summary>
    public static string StripToolCallIdAndMetadata(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var startIndex = 0;

        // Skip ToolCallId line
        if (lines.Length > startIndex && lines[startIndex].StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        // Skip Tool status line
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Tool `", StringComparison.Ordinal))
            startIndex++;
        // Skip empty line after status
        if (lines.Length > startIndex && lines[startIndex].Length == 0)
            startIndex++;
        // Skip Arguments line
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        // Skip Summary line
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        // Skip the trailing empty line after the metadata block
        if (lines.Length > startIndex && lines[startIndex].Length == 0)
            startIndex++;

        if (startIndex >= lines.Length)
            return string.Empty;

        return string.Join(Environment.NewLine, lines[startIndex..]);
    }

    private static int AppendAssistantModelMessages(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int assistantIndex,
        bool includeReasoningInModelContext)
    {
        var message = history[assistantIndex];
        var toolCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
        var reasoningContent = ReasoningInModelContext.Select(
            message.ReasoningContent,
            includeReasoningInModelContext,
            toolCalls is { Count: > 0 });
        if (toolCalls is not { Count: > 0 })
        {
            messages.Add(new AgentModelMessage("assistant", message.Content, ReasoningContent: reasoningContent));
            return assistantIndex;
        }

        var scanIndex = assistantIndex + 1;
        var toolMessages = new List<ChatMessage>();
        while (scanIndex < history.Count)
        {
            switch (history[scanIndex].Role)
            {
                case MessageRole.Tool:
                    toolMessages.Add(history[scanIndex]);
                    scanIndex++;
                    break;
                case MessageRole.Compaction:
                    scanIndex++;
                    break;
                default:
                    goto DoneScanning;
            }
        }

        DoneScanning:
        var toolByCallId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        foreach (var toolMessage in toolMessages)
        {
            var toolCallId = ExtractToolCallId(toolMessage.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                toolByCallId.TryAdd(toolCallId, toolMessage);
            }
        }

        messages.Add(new AgentModelMessage("assistant", message.Content, ToolCalls: toolCalls, ReasoningContent: reasoningContent));
        var toolImageMessages = new List<ChatMessage>();
        foreach (var toolCall in toolCalls)
        {
            var rawContent = toolByCallId.TryGetValue(toolCall.Id, out var toolMessage)
                ? toolMessage.Content
                : "Tool did not run or the result was not recorded.";
            var content = StripToolCallIdAndMetadata(rawContent);
            messages.Add(new AgentModelMessage("tool", content, toolCall.Id));
            if (toolMessage?.ImageAttachments is { Count: > 0 })
            {
                toolImageMessages.Add(toolMessage);
            }
        }

        foreach (var toolImageMessage in toolImageMessages)
        {
            AppendToolImageMessage(messages, toolImageMessage);
        }

        var consumed = new HashSet<string>(toolCalls.Select(call => call.Id), StringComparer.Ordinal);
        foreach (var toolMessage in toolMessages)
        {
            var toolCallId = ExtractToolCallId(toolMessage.Content);
            if (toolCallId is not null && consumed.Contains(toolCallId))
            {
                continue;
            }

            messages.Add(new AgentModelMessage("user", FormatToolResultAsUserContent(toolMessage.Content)));
            AppendToolImageMessage(messages, toolMessage);
        }

        return scanIndex - 1;
    }

    private static string FormatToolResultAsUserContent(string content) =>
        string.Join(Environment.NewLine, "[Tool output]", content);

    private static void AppendToolImageMessage(List<AgentModelMessage> messages, ChatMessage toolMessage)
    {
        if (toolMessage.ImageAttachments is not { Count: > 0 })
        {
            return;
        }

        var parts = BuildImageContentParts(
            ToolScreenshotCaption,
            toolMessage.ImageAttachments);
        if (parts.Count > 1)
        {
            messages.Add(new AgentModelMessage("user", parts));
        }
    }

    private static object BuildUserContent(ChatMessage message)
    {
        if (message.ImageAttachments is not { Count: > 0 })
        {
            return AppendUserTimestamp(message.Content, message.CreatedAt);
        }

        return BuildImageContentParts(
            AppendUserTimestamp(message.Content, message.CreatedAt),
            message.ImageAttachments);
    }

    private static List<object> BuildImageContentParts(
        string text,
        IReadOnlyList<ImageAttachment> images)
    {
        var parts = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = text
            }
        };

        foreach (var image in images)
        {
            var dataUrl = ImageAttachmentDataUrlResolver.ResolveDataUrl(image);
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                continue;
            }

            parts.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = dataUrl
                }
            });
        }

        return parts;
    }

    internal static string AppendUserTimestamp(string content, DateTimeOffset createdAt)
    {
        var local = AppTimeZone.ToChina(createdAt);
        var timestamp = $"[{local:yyyy-MM-dd HH:mm} {AppTimeZone.PromptLabel}]";
        return string.IsNullOrEmpty(content)
            ? timestamp
            : $"{content}{Environment.NewLine}{Environment.NewLine}{timestamp}";
    }

    private static string FormatArguments(AgentToolCall call)
    {
        if (!string.IsNullOrWhiteSpace(call.ArgumentsParseError))
        {
            var preview = FormatInvalidArgumentsPreview(call.RawArgumentsJson);
            return string.IsNullOrEmpty(preview)
                ? $"(invalid JSON) {call.ArgumentsParseError}"
                : $"(invalid JSON) {call.ArgumentsParseError}{Environment.NewLine}{preview}";
        }

        return call.Arguments.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, call.Arguments.Select(
                argument => $"{argument.Key}={JsonElementFormatter.FormatForDisplay(argument.Value, indented: false)}"));
    }

    private static string FormatInvalidArgumentsPreview(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return string.Empty;
        }

        const int head = 120;
        const int tail = 120;
        if (rawJson.Length <= head + tail + 3)
        {
            return rawJson;
        }

        return rawJson[..head] + "..." + rawJson[^tail..];
    }
}
