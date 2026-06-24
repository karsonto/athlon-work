using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>将 <see cref="AgentStreamEvent"/> 与历史消息序列化为 AG-UI 兼容 JSON，供 WebChatView 的 handleEvent 消费。</summary>
internal static class ChatEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(AgentStreamEvent streamEvent) =>
        streamEvent switch
        {
            AgentStreamEvent.RunStarted e => SerializeAgui("RUN_STARTED", new { threadId = e.SessionId, runId = e.RunId }),
            AgentStreamEvent.RunFinished e => SerializeAgui("RUN_FINISHED", new { threadId = e.SessionId, runId = e.RunId }),
            AgentStreamEvent.TextMessageStart e => SerializeAgui("TEXT_MESSAGE_START", new { messageId = e.MessageId, role = e.Role }),
            AgentStreamEvent.TextMessageContent e => SerializeAgui("TEXT_MESSAGE_CONTENT", new { messageId = e.MessageId, delta = e.Delta }),
            AgentStreamEvent.TextMessageEnd e => SerializeAgui("TEXT_MESSAGE_END", new { messageId = e.MessageId }),
            AgentStreamEvent.ReasoningMessageStart e => SerializeAgui("REASONING_MESSAGE_START", new { messageId = e.MessageId, role = e.Role }),
            AgentStreamEvent.ReasoningMessageContent e => SerializeAgui("REASONING_MESSAGE_CONTENT", new { messageId = e.MessageId, delta = e.Delta }),
            AgentStreamEvent.ReasoningMessageEnd e => SerializeAgui("REASONING_MESSAGE_END", new { messageId = e.MessageId }),
            AgentStreamEvent.ToolCallStart e => SerializeAgui("TOOL_CALL_START", new { toolCallId = e.ToolCallId, toolCallName = e.ToolName }),
            AgentStreamEvent.ToolCallArgs e => SerializeAgui("TOOL_CALL_ARGS", new { toolCallId = e.ToolCallId, delta = e.Delta }),
            AgentStreamEvent.ToolCallEnd e => SerializeAgui("TOOL_CALL_END", new { toolCallId = e.ToolCallId }),
            AgentStreamEvent.ToolCallResult e => SerializeAgui("TOOL_CALL_RESULT", new { toolCallId = e.ToolCallId, content = e.Content, messageId = e.MessageId }),
            AgentStreamEvent.ToolCallOutput e => SerializeAgui("TOOL_CALL_OUTPUT", new { toolCallId = e.ToolCallId, delta = e.Delta }),
            _ => "{}"
        };

    public static string SerializeResetTimeline() =>
        SerializeAgui("RESET_TIMELINE", new { });

    public static string SerializeUserMessage(ChatMessageViewModel message)
    {
        var content = message.Content;
        if (!string.IsNullOrWhiteSpace(message.UserAttachmentSummary))
        {
            content = string.IsNullOrWhiteSpace(content)
                ? message.UserAttachmentSummary
                : $"{content}\n{message.UserAttachmentSummary}";
        }

        return SerializeAgui("USER_MESSAGE", new { messageId = message.MessageId, content });
    }

    public static string SerializeStaticAssistantHtml(ChatMessageViewModel message) =>
        SerializeAgui("STATIC_ASSISTANT_HTML", new
        {
            messageId = message.MessageId,
            markdownB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Content)),
            htmlB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(MarkdownHtmlRenderer.ToHtmlFragment(message.Content))),
            createIfMissing = true
        });

    public static string SerializeToolResultMarkdown(ChatMessageViewModel message)
    {
        var toolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;
        var detail = !string.IsNullOrWhiteSpace(message.ToolDetailExpandedDisplay)
            ? message.ToolDetailExpandedDisplay
            : !string.IsNullOrWhiteSpace(message.ToolDetail)
                ? message.ToolDetail
                : message.ToolSummary;
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "{}";
        }

        return SerializeAgui("TOOL_CALL_RESULT", new
        {
            toolCallId,
            content = detail,
            messageId = message.MessageId,
            header = message.ToolHeader,
            summary = message.ToolSummary,
            markdownB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(detail)),
            htmlB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(MarkdownHtmlRenderer.ToHtmlFragment(detail)))
        });
    }

    public static string SerializeEventsToJsonArray(IReadOnlyList<string> eventJsonStrings)
    {
        if (eventJsonStrings.Count == 0)
        {
            return "[]";
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var eventJson in eventJsonStrings)
            {
                using var doc = JsonDocument.Parse(eventJson);
                doc.RootElement.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static IReadOnlyList<string> BuildReplayEvents(IReadOnlyList<ChatMessageViewModel> messages)
    {
        var events = new List<string> { SerializeResetTimeline() };
        foreach (var message in messages)
        {
            if (message.IsHiddenPlaceholder)
            {
                continue;
            }

            events.AddRange(BuildReplayEventsForMessage(message));
        }

        return events;
    }

    private static IEnumerable<string> BuildReplayEventsForMessage(ChatMessageViewModel message)
    {
        if (message.IsUser)
        {
            yield return SerializeUserMessage(message);
            yield break;
        }

        if (message.IsTool || message.IsCompaction)
        {
            foreach (var evt in BuildToolReplayEvents(message))
            {
                yield return evt;
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(message.ReasoningContent))
        {
            yield return SerializeAgui("REASONING_MESSAGE_START", new { messageId = message.MessageId, role = "assistant" });
            yield return SerializeAgui("REASONING_MESSAGE_CONTENT", new { messageId = message.MessageId, delta = message.ReasoningContent });
            yield return SerializeAgui("REASONING_MESSAGE_END", new { messageId = message.MessageId });
        }

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            yield return SerializeAgui("STATIC_ASSISTANT_HTML", new
            {
                messageId = message.MessageId,
                markdownB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Content)),
                htmlB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(MarkdownHtmlRenderer.ToHtmlFragment(message.Content))),
                createIfMissing = true
            });
        }
    }

    private static IEnumerable<string> BuildToolReplayEvents(ChatMessageViewModel message)
    {
        var toolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;
        var toolName = message.IsCompaction
            ? (string.IsNullOrWhiteSpace(message.CompactionCardTitle) ? "上下文压缩" : message.CompactionCardTitle)
            : string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : message.ToolName;

        yield return SerializeAgui("TOOL_CALL_START", new { toolCallId, toolCallName = toolName });

        if (!string.IsNullOrWhiteSpace(message.ToolArgumentsText) && message.ToolArgumentsText != "…")
        {
            yield return SerializeAgui("TOOL_CALL_ARGS", new { toolCallId, delta = message.ToolArgumentsText });
        }

        yield return SerializeAgui("TOOL_CALL_END", new { toolCallId });

        var detail = !string.IsNullOrWhiteSpace(message.ToolDetailExpandedDisplay)
            ? message.ToolDetailExpandedDisplay
            : !string.IsNullOrWhiteSpace(message.ToolDetail)
                ? message.ToolDetail
                : message.ToolSummary;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            yield return SerializeAgui("TOOL_CALL_RESULT", new
            {
                toolCallId,
                content = detail,
                messageId = message.MessageId,
                header = message.ToolHeader,
                summary = message.ToolSummary,
                markdownB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(detail)),
                htmlB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(MarkdownHtmlRenderer.ToHtmlFragment(detail)))
            });
        }
    }

    private static string SerializeAgui(string type, object payload)
    {
        var json = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            foreach (var property in json.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
