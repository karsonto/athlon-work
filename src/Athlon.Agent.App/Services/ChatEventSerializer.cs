using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
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
            AgentStreamEvent.ToolCallEnd e => SerializeAgui("TOOL_CALL_END", new { toolCallId = e.ToolCallId, status = "running" }),
            AgentStreamEvent.ToolCallResult e => SerializeAgui("TOOL_CALL_RESULT", new
            {
                toolCallId = e.ToolCallId,
                content = e.Content,
                messageId = e.MessageId,
                status = ParseToolStatusFromContent(e.Content)
            }),
            AgentStreamEvent.ToolCallOutput e => SerializeAgui("TOOL_CALL_OUTPUT", new { toolCallId = e.ToolCallId, delta = e.Delta }),
            _ => "{}"
        };

    public static string SerializeResetTimeline() =>
        SerializeAgui("RESET_TIMELINE", new { });

    public static string SerializeUserMessage(ChatMessageViewModel message)
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
        var content = message.Content;
        if (images.Count == 0 && !string.IsNullOrWhiteSpace(message.UserAttachmentSummary))
        {
            content = string.IsNullOrWhiteSpace(content)
                ? message.UserAttachmentSummary
                : $"{content}\n{message.UserAttachmentSummary}";
        }

        return SerializeAgui("USER_MESSAGE", new
        {
            messageId = message.MessageId,
            content,
            images
        });
    }

    public static string SerializeTurnActivity(TurnActivitySummary summary, bool upsert = false)
    {
        var items = summary.Items.Select(item => new
        {
            kind = item.Kind.ToString().ToLowerInvariant(),
            verb = LocalizeActivityVerb(item.Kind),
            detail = item.Detail,
            path = item.Path,
            added = item.Added,
            removed = item.Removed,
            body = item.Body,
            status = item.Status,
            statusLabel = item.Status is null ? null : LocalizeActivityStatus(item.Status),
            lines = item.DiffLines?.Select(line => new
            {
                kind = line.Kind,
                text = line.Text,
                count = line.Count
            })
        }).ToList();

        return SerializeAgui("TURN_ACTIVITY", new
        {
            upsert,
            editedFileCount = summary.EditedFileCount,
            exploredFileCount = summary.ExploredFileCount,
            searchCount = summary.SearchCount,
            commandCount = summary.CommandCount,
            thoughtCount = summary.ThoughtCount,
            totalAdded = summary.TotalAdded,
            totalRemoved = summary.TotalRemoved,
            items
        });
    }

    private static string LocalizeActivityVerb(TurnActivityKind kind) => kind switch
    {
        TurnActivityKind.Edited => Strings.Get("Chat_ActivityVerbEdited"),
        TurnActivityKind.Read => Strings.Get("Chat_ActivityVerbRead"),
        TurnActivityKind.Searched => Strings.Get("Chat_ActivityVerbSearched"),
        TurnActivityKind.Explored => Strings.Get("Chat_ActivityVerbExplored"),
        TurnActivityKind.Command => Strings.Get("Chat_ActivityVerbCommand"),
        TurnActivityKind.Thought => Strings.Get("Chat_ActivityVerbThought"),
        _ => kind.ToString()
    };

    private static string LocalizeActivityStatus(string status) => status switch
    {
        "preparing" => Strings.Get("Chat_ToolStatusPreparing"),
        "running" => Strings.Get("Chat_ToolStatusRunning"),
        "awaiting_approval" => Strings.Get("Chat_ToolApprovalPending"),
        "approval_denied" => Strings.Get("Chat_ToolApprovalDeniedStatus"),
        "failed" => Strings.Get("Chat_ToolStatusFailed"),
        "cancelled" => Strings.Get("Chat_ToolStatusCancelled"),
        "succeeded" => Strings.Get("Chat_ToolStatusSucceeded"),
        _ => status
    };

    public static string SerializeFilesChanged(IReadOnlyList<ModifiedFileViewModel> files, bool upsert = false)
    {
        if (files.Count == 0)
        {
            return SerializeAgui("FILES_CHANGED", new { upsert, files = Array.Empty<object>() });
        }

        var payload = files.Select(file => new
        {
            path = file.RelativePath,
            displayName = file.DisplayName,
            added = file.AddedCount,
            removed = file.RemovedCount,
            lines = UnifiedDiffDisplayParser.Parse(file.UnifiedDiffText, foldContext: true)
                .Select(line => new
                {
                    kind = line.Kind switch
                    {
                        DiffLineKind.Added => "added",
                        DiffLineKind.Removed => "removed",
                        DiffLineKind.Context => "context",
                        DiffLineKind.HunkHeader => "hunkHeader",
                        DiffLineKind.Header => "header",
                        DiffLineKind.Collapsed => "collapsed",
                        _ => "context"
                    },
                    text = line.Text,
                    count = line.CollapsedCount
                })
        }).ToList();

        return SerializeAgui("FILES_CHANGED", new { upsert, files = payload });
    }

    public static string SerializeStaticAssistantHtml(ChatMessageViewModel message, bool streaming = false) =>
        SerializeAgui("STATIC_ASSISTANT_HTML", new
        {
            messageId = message.MessageId,
            markdown = message.Content,
            html = MarkdownHtmlRenderer.ToHtmlFragment(message.Content),
            createIfMissing = true,
            streaming
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
            status = SerializeToolStatus(message.ToolCallStatus, message.ToolApprovalState),
            markdown = detail,
            html = RenderToolResultHtml(message, detail)
        });
    }

    public static string SerializeToolApprovalRequest(
        PendingToolApproval approval,
        string arguments) =>
        SerializeAgui("TOOL_APPROVAL_REQUEST", new
        {
            toolCallId = approval.ToolCallId,
            toolName = approval.ToolName,
            arguments
        });

    public static string SerializeToolApprovalResolved(
        string toolCallId,
        ToolApprovalDecision decision) =>
        SerializeAgui("TOOL_APPROVAL_RESOLVED", new
        {
            toolCallId,
            approved = decision == ToolApprovalDecision.Approved
        });

    private static string RenderToolResultHtml(ChatMessageViewModel message, string detail) =>
        message.IsCompaction && message.IsToolRunning
            ? MarkdownHtmlRenderer.ToPlainTextHtmlFragment(detail)
            : MarkdownHtmlRenderer.ToHtmlFragment(detail);

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

    public static string SerializeReplayCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        SerializeWebMessageCommand(
            "replay",
            BuildReplayEvents(messages, showToolCalls, activitySourceMessages: activitySourceMessages));

    public static string SerializeResetCommand() =>
        SerializeWebMessageCommand("reset", Array.Empty<string>());

    public static string SerializePrependCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        bool hasOlderMessages,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        SerializeWebMessageCommand(
            "prepend",
            BuildReplayEvents(
                messages,
                showToolCalls,
                includeReset: false,
                activitySourceMessages: activitySourceMessages),
            hasOlderMessages);

    public static string SerializeHistoryAvailabilityCommand(bool hasOlderMessages) =>
        JsonSerializer.Serialize(new
        {
            command = "historyAvailability",
            hasOlderMessages
        }, JsonOptions);

    public static IReadOnlyList<string> BuildReplayEvents(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        bool includeReset = true,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null)
    {
        var events = new List<string>();
        if (includeReset)
        {
            events.Add(SerializeResetTimeline());
        }

        var timeline = activitySourceMessages is { Count: > 0 }
            ? activitySourceMessages
                .Where(message => message.Role is MessageRole.User or MessageRole.Tool or MessageRole.Assistant)
                .Select(message => new ChatMessageViewModel(message))
                .ToList()
            : messages.ToList();

        var segment = new List<ChatMessageViewModel>();

        void EmitSegmentBubbles()
        {
            if (segment.Count == 0)
            {
                return;
            }

            var activity = TurnActivitySummaryBuilder.Build(segment);
            if (activity is { HasContent: true })
            {
                events.Add(SerializeTurnActivity(activity));
            }

            var files = SessionModifiedFilesTracker.BuildTurnFileGroups(segment);
            if (files is { Count: > 0 } && files[0].Count > 0)
            {
                events.Add(SerializeFilesChanged(files[0]));
            }

            segment.Clear();
        }

        foreach (var message in timeline)
        {
            if (message.IsHiddenPlaceholder)
            {
                continue;
            }

            if (message.IsUser)
            {
                EmitSegmentBubbles();
                events.AddRange(BuildReplayEventsForMessage(message));
                continue;
            }

            if (message.IsTool || message.IsCompaction)
            {
                if (TurnActivityClassifier.IsActivityTool(message.ToolName))
                {
                    segment.Add(message);
                    continue;
                }

                if (ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls, message))
                {
                    events.AddRange(BuildReplayEventsForMessage(message));
                }

                continue;
            }

            // Assistant: fold reasoning into the bubble that sits above this text output.
            if (message.HasReasoning)
            {
                segment.Add(new ChatMessageViewModel(
                    ChatMessage.Create(
                        MessageRole.Assistant,
                        string.Empty,
                        reasoningContent: message.ReasoningContent)));
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                EmitSegmentBubbles();
                events.AddRange(BuildReplayEventsForMessage(
                    new ChatMessageViewModel(
                        ChatMessage.CreateWithId(
                            message.MessageId,
                            MessageRole.Assistant,
                            message.Content))));
            }
        }

        EmitSegmentBubbles();
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

        // Reasoning is folded into TURN_ACTIVITY; do not emit standalone reasoning bubbles.

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            yield return SerializeAgui("STATIC_ASSISTANT_HTML", new
            {
                messageId = message.MessageId,
                markdown = message.Content,
                html = MarkdownHtmlRenderer.ToHtmlFragment(message.Content),
                createIfMissing = true
            });
        }
    }

    private static IEnumerable<string> BuildToolReplayEvents(ChatMessageViewModel message)
    {
        var toolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;
        var toolName = message.IsCompaction
            ? (string.IsNullOrWhiteSpace(message.CompactionCardTitle) ? Strings.Get("Chat_CompactionDefault") : message.CompactionCardTitle)
            : string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : message.ToolName;

        yield return SerializeAgui("TOOL_CALL_START", new { toolCallId, toolCallName = toolName });

        if (!string.IsNullOrWhiteSpace(message.ToolArgumentsText) && message.ToolArgumentsText != "…")
        {
            yield return SerializeAgui("TOOL_CALL_ARGS", new { toolCallId, delta = message.ToolArgumentsText });
        }

        yield return SerializeAgui("TOOL_CALL_END", new
        {
            toolCallId,
            status = SerializeToolStatus(message.ToolCallStatus, message.ToolApprovalState)
        });

        if (message.ToolApprovalState == ToolApprovalState.Pending)
        {
            yield return SerializeToolApprovalRequest(
                new PendingToolApproval(
                    toolCallId,
                    toolName,
                    ToolCallArguments.Empty,
                    ToolInvocationPolicy.Ask,
                    DateTimeOffset.UtcNow),
                message.ToolApprovalArgumentsPreview);
            yield break;
        }

        if (message.ToolApprovalState is ToolApprovalState.Approved or ToolApprovalState.Denied)
        {
            yield return SerializeToolApprovalResolved(
                toolCallId,
                message.ToolApprovalState == ToolApprovalState.Approved
                    ? ToolApprovalDecision.Approved
                    : ToolApprovalDecision.Denied);
        }

        if (message.IsCompaction && message.IsToolRunning)
        {
            yield break;
        }

        if (message.ToolApprovalState == ToolApprovalState.Denied)
        {
            yield break;
        }

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
                status = SerializeToolStatus(message.ToolCallStatus, message.ToolApprovalState),
                markdown = detail,
                html = RenderToolResultHtml(message, detail)
            });
        }
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

    private static string SerializeWebMessageCommand(
        string command,
        IReadOnlyList<string> events,
        bool? hasOlderMessages = null)
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

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
