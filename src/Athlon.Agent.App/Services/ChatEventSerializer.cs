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
    private static readonly object ReplayCacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> ReplayEventsCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReadOnlyList<ReplayTurnSegment>> ReplaySegmentsCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> ReplayCacheOrder = new();
    private const int ReplayCacheCapacity = 32;

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
            verb = item.Kind == TurnActivityKind.Tool
                ? item.Verb
                : LocalizeActivityVerb(item.Kind),
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
            durationMs = summary.DurationMs,
            items
        });
    }

    public static string SerializeRemoveAssistantBubbles(IReadOnlyList<string> messageIds) =>
        SerializeAgui("REMOVE_ASSISTANT_BUBBLES", new { messageIds });

    private static string LocalizeActivityVerb(TurnActivityKind kind) => kind switch
    {
        TurnActivityKind.Edited => Strings.Get("Chat_ActivityVerbEdited"),
        TurnActivityKind.Read => Strings.Get("Chat_ActivityVerbRead"),
        TurnActivityKind.Searched => Strings.Get("Chat_ActivityVerbSearched"),
        TurnActivityKind.Explored => Strings.Get("Chat_ActivityVerbExplored"),
        TurnActivityKind.Command => Strings.Get("Chat_ActivityVerbCommand"),
        TurnActivityKind.Thought => Strings.Get("Chat_ActivityVerbThought"),
        TurnActivityKind.Narration => Strings.Get("Chat_ActivityVerbNarration"),
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

    public static string SerializeCompactionCheckpoint(ChatMessageViewModel message)
    {
        var id = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;

        return SerializeAgui("COMPACTION_CHECKPOINT", new
        {
            id,
            title = string.IsNullOrWhiteSpace(message.CompactionCardTitle)
                ? Strings.Get("Chat_CompactionDefault")
                : message.CompactionCardTitle,
            summary = message.ToolSummary,
            header = message.ToolHeader,
            detail = message.IsToolRunning ? string.Empty : message.ToolDetail,
            detailsLabel = Strings.Get("Chat_CompactionDetails"),
            status = SerializeToolStatus(message.ToolCallStatus, message.ToolApprovalState),
            running = message.IsToolRunning
        });
    }

    public static string SerializeToolResultMarkdown(ChatMessageViewModel message)
    {
        if (message.IsCompaction)
        {
            return SerializeCompactionCheckpoint(message);
        }

        var toolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;
        var detail = ResolveToolResultDetail(message);
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
            "replaceSurface",
            BuildReplayEvents(messages, showToolCalls, activitySourceMessages: activitySourceMessages));

    public static string SerializeAppendCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        SerializeWebMessageCommand(
            "appendEvents",
            BuildReplayEvents(
                messages,
                showToolCalls,
                includeReset: false,
                activitySourceMessages: activitySourceMessages));

    public static string SerializeEventsCommand(string command, IReadOnlyList<string> events) =>
        SerializeWebMessageCommand(command, events);

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
        var cacheKey = BuildReplayCacheKey(messages, showToolCalls, includeReset, activitySourceMessages);
        lock (ReplayCacheLock)
        {
            if (ReplayEventsCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var timeline = activitySourceMessages is { Count: > 0 }
            ? activitySourceMessages
                .Where(message => message.Role is MessageRole.User
                    or MessageRole.Tool
                    or MessageRole.Assistant
                    or MessageRole.Compaction)
                .Select(message => new ChatMessageViewModel(message))
                .ToList()
            : messages.ToList();
        var timelineKey = BuildReplayTimelineKey(timeline, showToolCalls);
        IReadOnlyList<ReplayTurnSegment> segments;
        lock (ReplayCacheLock)
        {
            if (!ReplaySegmentsCache.TryGetValue(timelineKey, out segments!))
            {
                segments = BuildReplaySegments(timeline, showToolCalls);
                AddReplaySegmentsToCache(timelineKey, segments);
            }
        }

        var events = BuildEventsFromSegments(segments, includeReset);
        lock (ReplayCacheLock)
        {
            AddReplayEventsToCache(cacheKey, events);
        }

        return events;
    }

    private static IReadOnlyList<ReplayTurnSegment> BuildReplaySegments(
        IReadOnlyList<ChatMessageViewModel> timeline,
        bool showToolCalls)
    {
        var segments = new List<ReplayTurnSegment>();
        var activitySegment = new List<ChatMessageViewModel>();
        var pendingToolCards = new List<string>();
        var pendingAssistants = new List<ChatMessageViewModel>();
        var finalAssistantMessageIds = FindFinalAssistantMessageIds(timeline);

        void FlushTurnIntermediate()
        {
            string? activityEvent = null;
            string? filesEvent = null;
            if (activitySegment.Count > 0)
            {
                var activity = TurnActivitySummaryBuilder.Build(activitySegment);
                if (activity is { HasContent: true })
                {
                    activityEvent = SerializeTurnActivity(activity);
                }

                var files = SessionModifiedFilesTracker.BuildTurnFileGroups(activitySegment);
                if (files is { Count: > 0 } && files[0].Count > 0)
                {
                    filesEvent = SerializeFilesChanged(files[0]);
                }

                activitySegment.Clear();
            }

            var assistantEvents = new List<string>();
            foreach (var assistant in pendingAssistants)
            {
                assistantEvents.AddRange(BuildReplayEventsForMessage(assistant));
            }

            if (activityEvent is not null
                || filesEvent is not null
                || pendingToolCards.Count > 0
                || assistantEvents.Count > 0)
            {
                segments.Add(new ReplayTurnSegment(
                    UserEvents: Array.Empty<string>(),
                    ActivityEvent: activityEvent,
                    FilesChangedEvent: filesEvent,
                    ToolEvents: pendingToolCards.ToArray(),
                    AssistantEvents: assistantEvents.ToArray(),
                    CompactionEvent: null));
            }

            pendingToolCards.Clear();
            pendingAssistants.Clear();
        }

        foreach (var message in timeline)
        {
            if (message.IsHiddenPlaceholder)
            {
                continue;
            }

            if (message.IsUser)
            {
                FlushTurnIntermediate();
                segments.Add(new ReplayTurnSegment(
                    UserEvents: BuildReplayEventsForMessage(message).ToArray(),
                    ActivityEvent: null,
                    FilesChangedEvent: null,
                    ToolEvents: Array.Empty<string>(),
                    AssistantEvents: Array.Empty<string>(),
                    CompactionEvent: null));
                continue;
            }

            if (message.IsCompaction)
            {
                if (ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(message))
                {
                    FlushTurnIntermediate();
                    segments.Add(new ReplayTurnSegment(
                        UserEvents: Array.Empty<string>(),
                        ActivityEvent: null,
                        FilesChangedEvent: null,
                        ToolEvents: Array.Empty<string>(),
                        AssistantEvents: Array.Empty<string>(),
                        CompactionEvent: SerializeCompactionCheckpoint(message)));
                }

                continue;
            }

            if (message.IsTool)
            {
                if (ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls, message))
                {
                    pendingToolCards.AddRange(BuildReplayEventsForMessage(message));
                    continue;
                }

                if (TurnActivityClassifier.IsActivityTool(message.ToolName))
                {
                    activitySegment.Add(message);
                }

                continue;
            }

            if (message.HasReasoning)
            {
                activitySegment.Add(new ChatMessageViewModel(
                    ChatMessage.Create(
                        MessageRole.Assistant,
                        string.Empty,
                        reasoningContent: message.ReasoningContent)));
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                var assistantVm = new ChatMessageViewModel(
                    ChatMessage.CreateWithId(
                        message.MessageId,
                        MessageRole.Assistant,
                        message.Content));
                if (finalAssistantMessageIds.Contains(message.MessageId))
                {
                    pendingAssistants.Add(assistantVm);
                }
                else
                {
                    activitySegment.Add(assistantVm);
                }
            }
        }

        FlushTurnIntermediate();
        return segments;
    }

    private static IReadOnlyList<string> BuildEventsFromSegments(
        IReadOnlyList<ReplayTurnSegment> segments,
        bool includeReset)
    {
        var events = new List<string>();
        if (includeReset)
        {
            events.Add(SerializeResetTimeline());
        }

        foreach (var segment in segments)
        {
            events.AddRange(segment.UserEvents);
            if (segment.ActivityEvent is not null)
            {
                events.Add(segment.ActivityEvent);
            }

            if (segment.FilesChangedEvent is not null)
            {
                events.Add(segment.FilesChangedEvent);
            }

            events.AddRange(segment.ToolEvents);
            events.AddRange(segment.AssistantEvents);
            if (segment.CompactionEvent is not null)
            {
                events.Add(segment.CompactionEvent);
            }
        }

        return events;
    }

    private static string BuildReplayCacheKey(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        bool includeReset,
        IReadOnlyList<ChatMessage>? activitySourceMessages) =>
        string.Join(
            "|",
            BuildReplayTimelineKey(
                activitySourceMessages is { Count: > 0 }
                    ? activitySourceMessages.Select(message => new ChatMessageViewModel(message)).ToList()
                    : messages.ToList(),
                showToolCalls),
            includeReset ? "reset" : "noreset",
            System.Globalization.CultureInfo.CurrentUICulture.Name);

    private static string BuildReplayTimelineKey(
        IReadOnlyList<ChatMessageViewModel> timeline,
        bool showToolCalls)
    {
        var builder = new StringBuilder();
        builder.Append(showToolCalls ? '1' : '0');
        foreach (var message in timeline)
        {
            builder.Append('|');
            builder.Append(message.MessageId);
            builder.Append(':');
            builder.Append(message.Role);
            builder.Append(':');
            builder.Append(message.Content?.Length ?? 0);
            builder.Append(':');
            builder.Append(message.ReasoningContent?.Length ?? 0);
            builder.Append(':');
            builder.Append(message.ToolCallId);
        }

        return builder.ToString();
    }

    private static void AddReplayEventsToCache(string key, IReadOnlyList<string> events)
    {
        ReplayEventsCache[key] = events;
        ReplayCacheOrder.Enqueue("events:" + key);
        TrimReplayCaches();
    }

    private static void AddReplaySegmentsToCache(string key, IReadOnlyList<ReplayTurnSegment> segments)
    {
        ReplaySegmentsCache[key] = segments;
        ReplayCacheOrder.Enqueue("segments:" + key);
        TrimReplayCaches();
    }

    private static void TrimReplayCaches()
    {
        while (ReplayCacheOrder.Count > ReplayCacheCapacity)
        {
            var key = ReplayCacheOrder.Dequeue();
            if (key.StartsWith("events:", StringComparison.Ordinal))
            {
                ReplayEventsCache.Remove(key["events:".Length..]);
            }
            else if (key.StartsWith("segments:", StringComparison.Ordinal))
            {
                ReplaySegmentsCache.Remove(key["segments:".Length..]);
            }
        }
    }

    private sealed record ReplayTurnSegment(
        IReadOnlyList<string> UserEvents,
        string? ActivityEvent,
        string? FilesChangedEvent,
        IReadOnlyList<string> ToolEvents,
        IReadOnlyList<string> AssistantEvents,
        string? CompactionEvent);

    /// <summary>
    /// Per user turn: assistants that should remain bubbles (not folded into activity).
    /// Turns with tools/reasoning keep only the last assistant text as the final bubble;
    /// chat-only turns keep every assistant text as bubbles.
    /// </summary>
    private static HashSet<string> FindFinalAssistantMessageIds(IReadOnlyList<ChatMessageViewModel> timeline)
    {
        var finals = new HashSet<string>(StringComparer.Ordinal);
        var turnHasActivity = false;
        var turnAssistantIds = new List<string>();

        void CloseTurn()
        {
            if (turnAssistantIds.Count > 0)
            {
                if (turnHasActivity)
                {
                    finals.Add(turnAssistantIds[^1]);
                }
                else
                {
                    foreach (var id in turnAssistantIds)
                    {
                        finals.Add(id);
                    }
                }
            }

            turnHasActivity = false;
            turnAssistantIds.Clear();
        }

        foreach (var message in timeline)
        {
            if (message.IsHiddenPlaceholder)
            {
                continue;
            }

            if (message.IsUser || message.IsCompaction)
            {
                CloseTurn();
                continue;
            }

            if (message.IsTool && TurnActivityClassifier.IsActivityTool(message.ToolName))
            {
                turnHasActivity = true;
            }
            else if (!message.IsTool && message.HasReasoning)
            {
                turnHasActivity = true;
            }

            if (!message.IsTool && !string.IsNullOrWhiteSpace(message.Content))
            {
                turnAssistantIds.Add(message.MessageId);
            }
        }

        CloseTurn();
        return finals;
    }

    private static IEnumerable<string> BuildReplayEventsForMessage(ChatMessageViewModel message)
    {
        if (message.IsUser)
        {
            yield return SerializeUserMessage(message);
            yield break;
        }

        if (message.IsCompaction)
        {
            if (ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(message))
            {
                yield return SerializeCompactionCheckpoint(message);
            }

            yield break;
        }

        if (message.IsTool)
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
        var toolName = string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : message.ToolName;

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

        var detail = ResolveToolResultDetail(message);
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

    private static string ResolveToolResultDetail(ChatMessageViewModel message)
    {
        if (message.IsTool || message.IsCompaction)
        {
            var formatted = ToolResultDisplayFormatter.FormatDetail(message.Content);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                var limit = message.IsExpanded
                    ? ChatMessageViewModel.MaxToolDetailDisplayChars
                    : 4_096;
                return ChatMessageViewModel.TruncateToolDetailForDisplay(formatted, limit);
            }
        }

        return !string.IsNullOrWhiteSpace(message.ToolDetailExpandedDisplay)
            ? message.ToolDetailExpandedDisplay
            : !string.IsNullOrWhiteSpace(message.ToolDetail)
                ? message.ToolDetail
                : message.ToolSummary;
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
