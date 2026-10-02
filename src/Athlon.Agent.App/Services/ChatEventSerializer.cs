using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>将 <see cref="AgentStreamEvent"/> 与历史消息序列化为 AG-UI 兼容 JSON，供 WebChatView 的 handleEvent 消费。</summary>
internal static partial class ChatEventSerializer
{
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
            AgentStreamEvent.ToolCallEnd e => SerializeAgui("TOOL_CALL_END", new { toolCallId = e.ToolCallId, status = "running" }),
            AgentStreamEvent.ToolCallResult e => SerializeAgui("TOOL_CALL_RESULT", new
            {
                toolCallId = e.ToolCallId,
                content = e.Content,
                messageId = e.MessageId,
                status = ParseToolStatusFromContent(e.Content),
                images = ResolveTimelineImages(e.ImageAttachments)
            }),
            AgentStreamEvent.ToolCallOutput e => SerializeAgui("TOOL_CALL_OUTPUT", new { toolCallId = e.ToolCallId, delta = e.Delta }),
            AgentStreamEvent.OverflowRetrySkipped e => SerializeAgui("OVERFLOW_RETRY_SKIPPED", new
            {
                failedTokens = e.FailedTokens,
                retryTokens = e.RetryTokens,
                reason = e.Reason,
                message = Strings.Get("Chat_OverflowRetrySkipped")
            }),
            _ => "{}"
        };

    public static string SerializeResetTimeline() =>
        SerializeAgui("RESET_TIMELINE", new { });

    public static string SerializeUserMessage(ChatMessageViewModel message, long? seq = null)
    {
        var images = message.ImageAttachments
            .Select(image =>
            {
                var url = ImageAttachmentDataUrlResolver.ResolveDataUrl(image);
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }

                return new
                {
                    fileName = image.FileName,
                    mimeType = image.MimeType,
                    url
                };
            })
            .Where(image => image is not null)
            .ToList();

        // Prefer rendering real thumbnails; omit the "N image(s) attached" text fallback.
        var content = ToTimelineUserContent(message.Content);
        if (images.Count == 0 && !string.IsNullOrWhiteSpace(message.UserAttachmentSummary))
        {
            content = string.IsNullOrWhiteSpace(content)
                ? message.UserAttachmentSummary
                : $"{content}\n{message.UserAttachmentSummary}";
        }

        return SerializeAgui("USER_MESSAGE", new
        {
            seq,
            messageId = message.MessageId,
            content,
            mentions = BuildUserMentions(content) is { Length: > 0 } fileMentions ? fileMentions : null,
            images,
            startedAt = FormatStartedAt(message.CreatedAtUtc)
        });
    }

    private sealed record UserMentionDto(
        int Start,
        int Length,
        string FileName,
        string Path,
        string Kind,
        string? IconKind);

    private static UserMentionDto[] BuildUserMentions(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var spans = ComposerMentionDocument.ParseMentions(content);
        if (spans.Count == 0)
        {
            return [];
        }

        var mentions = new UserMentionDto[spans.Count];
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            mentions[i] = new UserMentionDto(
                span.Start,
                span.Length,
                span.DisplayName,
                span.RelativePath,
                span.Kind.ToString().ToLowerInvariant(),
                span.Kind == ComposerMentionKind.File ? span.IconKind.ToString() : null);
        }

        return mentions;
    }

    private static string ToTimelineUserContent(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var stripped = SkillComposerExpander.StripForDisplay(content);
        return McpComposerExpander.StripForDisplay(stripped);
    }

    public static string FormatStartedAt(DateTimeOffset instant) =>
        AppTimeZone.ToChina(instant).ToString("yyyy-MM-dd HH:mm:ss");

    public static int? ComputeResponseDurationMs(DateTimeOffset turnStartedAt, DateTimeOffset finishedAt)
    {
        var ms = (int)Math.Round((finishedAt - turnStartedAt).TotalMilliseconds);
        return ms > 0 ? ms : null;
    }


    private static string SerializeToolStatus(ToolCallDisplayStatus status, ToolApprovalState approvalState = ToolApprovalState.None) =>
        approvalState switch
        {
            ToolApprovalState.Pending => "awaiting_approval",
            ToolApprovalState.Denied => "approval_denied",
            _ => status switch
            {
                ToolCallDisplayStatus.Running => "running",
                ToolCallDisplayStatus.Failed => "failed",
                ToolCallDisplayStatus.Cancelled => "cancelled",
                ToolCallDisplayStatus.Preparing => "preparing",
                ToolCallDisplayStatus.AwaitingApproval => "awaiting_approval",
                ToolCallDisplayStatus.ApprovalDenied => "approval_denied",
                _ => "succeeded"
            }
        };

    private static string ParseToolStatusFromContent(string content)
    {
        ToolMessageDisplayParser.ParseToolContent(
            content,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out var status);
        return SerializeToolStatus(status);
    }

    private static List<object> ResolveTimelineImages(IReadOnlyList<ImageAttachment>? images)
    {
        var list = new List<object>();
        if (images is not { Count: > 0 })
        {
            return list;
        }

        foreach (var image in images)
        {
            var url = ImageAttachmentDataUrlResolver.ResolveDataUrl(image);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            list.Add(new
            {
                fileName = image.FileName,
                mimeType = image.MimeType,
                url
            });
        }

        return list;
    }

    private static string SerializeAgui(string type, object payload)
    {
        var json = JsonSerializer.SerializeToElement(payload, AppJson.Options);
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

    private static string SerializeWebMessageCommand(
        string command,
        IReadOnlyList<string> events,
        bool? hasOlderMessages = null,
        int? renderGeneration = null,
        bool replayComplete = false,
        string? sessionId = null,
        string? revision = null)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WritePropertyName("events");
            writer.WriteStartArray();
            foreach (var eventJson in events)
            {
                using var document = JsonDocument.Parse(eventJson);
                document.RootElement.WriteTo(writer);
            }

            writer.WriteEndArray();
            if (hasOlderMessages is not null)
            {
                writer.WriteBoolean("hasOlderMessages", hasOlderMessages.Value);
            }

            if (renderGeneration is not null)
            {
                writer.WriteNumber("renderGeneration", renderGeneration.Value);
            }

            if (!string.IsNullOrEmpty(sessionId))
            {
                writer.WriteString("sessionId", sessionId);
            }

            if (replayComplete)
            {
                writer.WriteBoolean("replayComplete", true);
            }

            if (!string.IsNullOrEmpty(revision))
            {
                writer.WriteString("revision", revision);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
