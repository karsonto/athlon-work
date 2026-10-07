namespace Athlon.Agent.Core;

internal static class ModelMessageBuilder
{
    internal const string ToolScreenshotCaption =
        "[Computer Use screenshot returned by the preceding tool result.]";

    internal const string BrowserScreenshotCaption =
        "[Browser tab screenshot returned by the preceding tool result.]";

    public static List<AgentModelMessage> BuildForSession(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext,
        bool stripUiTrees = false) =>
        BuildModelMessages(environmentPrompt, history, includeReasoningInModelContext, stripUiTrees);

    public static List<AgentModelMessage> BuildModelMessages(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext = false,
        bool stripUiTrees = false)
    {
        var messages = new List<AgentModelMessage>
        {
            new("system", environmentPrompt)
        };

        AppendHistoryRange(messages, history, 0, includeReasoningInModelContext, stripUiTrees);
        return messages;
    }

    public static int AppendHistoryMessage(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int index,
        bool includeReasoningInModelContext,
        bool stripUiTrees = false) =>
        AppendHistoryMessageCore(messages, history, index, includeReasoningInModelContext, stripUiTrees);

    private static void AppendHistoryRange(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int startIndex,
        bool includeReasoningInModelContext,
        bool stripUiTrees)
    {
        for (var index = startIndex; index < history.Count; index++)
        {
            index = AppendHistoryMessageCore(messages, history, index, includeReasoningInModelContext, stripUiTrees);
        }
    }

    private static int AppendHistoryMessageCore(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int index,
        bool includeReasoningInModelContext,
        bool stripUiTrees)
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
                return AppendAssistantModelMessages(messages, history, index, includeReasoningInModelContext, stripUiTrees);
            case MessageRole.Tool:
            {
                if (IsRunningToolResult(message.Content))
                {
                    return index;
                }

                var toolCallId = ExtractToolCallId(message.Content);
                if (toolCallId is not null)
                {
                    messages.Add(new AgentModelMessage("tool", HistoryToolBody(message.Content, stripUiTrees), toolCallId));
                }
                else
                {
                    messages.Add(new AgentModelMessage("user", HistoryToolBody(FormatToolResultAsUserContent(message.Content), stripUiTrees)));
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

    public static string FormatRunningToolPlaceholder(AgentToolCall call) =>
        string.Join(Environment.NewLine, new[]
        {
            $"ToolCallId: {call.Id}",
            $"Tool `{call.Name}` running.",
            "",
            $"Arguments: {FormatArguments(call)}",
            "Summary: 执行中",
            "",
            string.Empty
        });

    public static bool IsRunningToolResult(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith("Tool `", StringComparison.Ordinal))
            {
                continue;
            }

            return line.Contains(" running.", StringComparison.OrdinalIgnoreCase)
                || line.EndsWith(" running", StringComparison.OrdinalIgnoreCase);
        }

        return false;
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
        bool includeReasoningInModelContext,
        bool stripUiTrees)
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
            if (IsRunningToolResult(toolMessage.Content))
            {
                continue;
            }

            var toolCallId = ExtractToolCallId(toolMessage.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                toolByCallId.TryAdd(toolCallId, toolMessage);
            }
        }

        messages.Add(new AgentModelMessage("assistant", message.Content, ToolCalls: toolCalls, ReasoningContent: reasoningContent));
        foreach (var toolCall in toolCalls)
        {
            var rawContent = toolByCallId.TryGetValue(toolCall.Id, out var toolMessage)
                ? toolMessage.Content
                : "Tool did not run or the result was not recorded.";
            messages.Add(new AgentModelMessage("tool", HistoryToolBody(rawContent, stripUiTrees), toolCall.Id));
        }

        var consumed = new HashSet<string>(toolCalls.Select(call => call.Id), StringComparer.Ordinal);
        foreach (var toolMessage in toolMessages)
        {
            var toolCallId = ExtractToolCallId(toolMessage.Content);
            if (toolCallId is not null && consumed.Contains(toolCallId))
            {
                continue;
            }

            messages.Add(new AgentModelMessage(
                "user",
                HistoryToolBody(FormatToolResultAsUserContent(toolMessage.Content), stripUiTrees)));
        }

        return scanIndex - 1;
    }

    private static string FormatToolResultAsUserContent(string content) =>
        string.Join(Environment.NewLine, "[Tool output]", content);

    private static string HistoryToolBody(string content, bool stripUiTrees)
    {
        var body = StripToolCallIdAndMetadata(content);
        return stripUiTrees ? Compaction.RequestHistoryHygiene.StripUiTree(body) : body;
    }

    /// <summary>
    /// Appends the newest tool screenshots and, when history stored only UI-tree summaries,
    /// the newest full trees. Both sit after history so earlier messages stay byte-stable.
    /// </summary>
    public static void AppendRetainedToolMedia(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int maxImages,
        bool retainFullUiTrees,
        int uiTreeRetention)
    {
        if (retainFullUiTrees && uiTreeRetention > 0)
        {
            var bodies = new List<string>();
            foreach (var message in history)
            {
                if (message.Role != MessageRole.Tool || string.IsNullOrEmpty(message.Content))
                {
                    continue;
                }

                var body = StripToolCallIdAndMetadata(message.Content);
                if (string.Equals(Compaction.RequestHistoryHygiene.StripUiTree(body), body, StringComparison.Ordinal))
                {
                    continue;
                }

                bodies.Add(body);
            }

            if (bodies.Count > uiTreeRetention)
            {
                bodies = bodies.GetRange(bodies.Count - uiTreeRetention, uiTreeRetention);
            }

            foreach (var body in bodies)
            {
                messages.Add(new AgentModelMessage(
                    "user",
                    "[Retained UI tree for the preceding observation.]\n" + body));
            }
        }

        AppendRetainedScreenshots(messages, history, maxImages);
    }

    private static void AppendRetainedScreenshots(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int maxImages)
    {
        maxImages = Math.Max(0, maxImages);
        var shots = new List<(ChatMessage Source, ImageAttachment Image)>();
        foreach (var message in history)
        {
            if (message.Role != MessageRole.Tool || message.ImageAttachments is not { Count: > 0 } images)
            {
                continue;
            }

            foreach (var image in images)
            {
                shots.Add((message, image));
            }
        }

        if (shots.Count > maxImages)
        {
            shots = shots.GetRange(shots.Count - maxImages, maxImages);
        }

        ChatMessage? current = null;
        var batch = new List<ImageAttachment>();
        foreach (var shot in shots)
        {
            if (current is not null && !ReferenceEquals(current, shot.Source))
            {
                AppendToolImageMessage(messages, batch);
                batch = [];
            }

            current = shot.Source;
            batch.Add(shot.Image);
        }

        if (current is not null && batch.Count > 0)
        {
            AppendToolImageMessage(messages, batch);
        }
    }

    private static void AppendToolImageMessage(
        List<AgentModelMessage> messages,
        IReadOnlyList<ImageAttachment> images)
    {
        if (images.Count == 0)
        {
            return;
        }

        var caption = images.Any(image =>
            image.FileName.StartsWith(Browser.BrowserFrameFiles.Prefix, StringComparison.Ordinal))
            ? BrowserScreenshotCaption
            : ToolScreenshotCaption;
        var parts = BuildImageContentParts(caption, images);
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
