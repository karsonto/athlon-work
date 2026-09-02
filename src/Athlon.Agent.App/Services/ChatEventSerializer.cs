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
        var content = ToTimelineUserContent(message.Content);
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
            messageId = item.MessageId,
            toolCallId = item.ToolCallId,
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

    public static string SerializeStaticAssistantHtml(
        ChatMessageViewModel message,
        bool streaming = false,
        int? responseDurationMs = null) =>
        SerializeAgui("STATIC_ASSISTANT_HTML", new
        {
            messageId = message.MessageId,
            markdown = message.Content,
            html = MarkdownHtmlRenderer.ToHtmlFragment(message.Content),
            createIfMissing = true,
            streaming,
            responseDurationMs = streaming ? null : responseDurationMs
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

    public static string SerializePlanClarifyRequest(PlanClarification clarification, bool resolved = false, string? summary = null) =>
        SerializeAgui("PLAN_CLARIFY_REQUEST", new
        {
            requestId = clarification.RequestId,
            allowFreeText = clarification.AllowFreeText,
            resolved,
            summary,
            questions = clarification.Questions.Select(q => new
            {
                id = q.Id,
                prompt = q.Prompt,
                allowMultiple = q.AllowMultiple,
                options = q.Options.Select(o => new { id = o.Id, label = o.Label }).ToList()
            }).ToList()
        });

    public static string SerializePlanClarifyResolved(string requestId, string? summary = null) =>
        SerializeAgui("PLAN_CLARIFY_RESOLVED", new
        {
            requestId,
            summary
        });

    public static string SerializePlanReady(PlanRun run) =>
        SerializeAgui("PLAN_READY", new
        {
            runId = run.Id,
            title = run.Title,
            overview = run.Overview,
            planPath = run.PlanPath,
            markdown = run.PlanMarkdown,
            html = MarkdownHtmlRenderer.ToHtmlFragment(run.PlanMarkdown),
            todos = run.Todos.Select(t => new { id = t.Id, content = t.Content }).ToList()
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
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        TimelineProjectionMode mode = TimelineProjectionMode.HighFidelity) =>
        SerializeWebMessageCommand(
            "replaceSurface",
            BuildReplayEvents(messages, showToolCalls, activitySourceMessages: activitySourceMessages, mode: mode));

    public static string SerializeAppendCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        TimelineProjectionMode mode = TimelineProjectionMode.HighFidelity) =>
        SerializeWebMessageCommand(
            "appendEvents",
            BuildReplayEvents(
                messages,
                showToolCalls,
                includeReset: false,
                activitySourceMessages: activitySourceMessages,
                mode: mode));

    public static string SerializeEventsCommand(
        string command,
        IReadOnlyList<string> events,
        int? renderGeneration = null,
        bool replayComplete = false) =>
        SerializeWebMessageCommand(
            command,
            events,
            renderGeneration: renderGeneration,
            replayComplete: replayComplete);

    public static string SerializeResetCommand() =>
        SerializeWebMessageCommand("reset", Array.Empty<string>());

    public static string SerializePrependCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        bool hasOlderMessages,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        TimelineProjectionMode mode = TimelineProjectionMode.HighFidelity) =>
        SerializeWebMessageCommand(
            "prepend",
            BuildReplayEvents(
                messages,
                showToolCalls,
                includeReset: false,
                activitySourceMessages: activitySourceMessages,
                mode: mode),
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
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        TimelineProjectionMode mode = TimelineProjectionMode.HighFidelity)
    {
        var cacheKey = BuildReplayCacheKey(messages, showToolCalls, includeReset, activitySourceMessages, mode);
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
        var timelineKey = BuildReplayTimelineKey(timeline, showToolCalls, mode);
        IReadOnlyList<ReplayTurnSegment> segments;
        lock (ReplayCacheLock)
        {
            if (!ReplaySegmentsCache.TryGetValue(timelineKey, out segments!))
            {
                segments = BuildReplaySegments(timeline, showToolCalls, mode);
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
        bool showToolCalls,
        TimelineProjectionMode mode)
    {
        var projected = ChatTimelineProjector.BuildSegments(timeline, showToolCalls, mode);
        var segments = new List<ReplayTurnSegment>(projected.Count);

        foreach (var segment in projected)
        {
            if (segment.UserMessages.Count > 0)
            {
                foreach (var user in segment.UserMessages)
                {
                    segments.Add(new ReplayTurnSegment(
                        UserEvents: BuildReplayEventsForMessage(user).ToArray(),
                        ActivityEvent: null,
                        FilesChangedEvent: null,
                        ToolEvents: Array.Empty<string>(),
                        AssistantEvents: Array.Empty<string>(),
                        CompactionEvent: null));
                }

                continue;
            }

            if (segment.CompactionMessage is { } compaction)
            {
                segments.Add(new ReplayTurnSegment(
                    UserEvents: Array.Empty<string>(),
                    ActivityEvent: null,
                    FilesChangedEvent: null,
                    ToolEvents: Array.Empty<string>(),
                    AssistantEvents: Array.Empty<string>(),
                    CompactionEvent: SerializeCompactionCheckpoint(compaction)));
                continue;
            }

            string? activityEvent = null;
            string? filesEvent = null;
            if (segment.ActivitySegment.Count > 0)
            {
                var activity = TurnActivitySummaryBuilder.Build(segment.ActivitySegment);
                if (activity is { HasContent: true })
                {
                    activityEvent = SerializeTurnActivity(activity);
                }

                var files = SessionModifiedFilesTracker.BuildTurnFileGroups(segment.ActivitySegment);
                if (files is { Count: > 0 } && files[0].Count > 0)
                {
                    filesEvent = SerializeFilesChanged(files[0]);
                }
            }

            var toolEvents = new List<string>();
            foreach (var tool in segment.ToolMessages)
            {
                toolEvents.AddRange(BuildReplayEventsForMessage(tool));
            }

            var assistantEvents = new List<string>();
            foreach (var assistant in segment.AssistantMessages)
            {
                var durationMs = segment.TurnUserCreatedAt is { } startedAt
                    ? ComputeResponseDurationMs(startedAt, assistant.CreatedAtUtc)
                    : null;
                assistantEvents.AddRange(BuildReplayEventsForMessage(assistant, durationMs));
            }

            if (activityEvent is not null
                || filesEvent is not null
                || toolEvents.Count > 0
                || assistantEvents.Count > 0)
            {
                segments.Add(new ReplayTurnSegment(
                    UserEvents: Array.Empty<string>(),
                    ActivityEvent: activityEvent,
                    FilesChangedEvent: filesEvent,
                    ToolEvents: toolEvents.ToArray(),
                    AssistantEvents: assistantEvents.ToArray(),
                    CompactionEvent: null));
            }
        }

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

            events.AddRange(segment.ToolEvents);
            events.AddRange(segment.AssistantEvents);

            if (segment.FilesChangedEvent is not null)
            {
                events.Add(segment.FilesChangedEvent);
            }

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
        IReadOnlyList<ChatMessage>? activitySourceMessages,
        TimelineProjectionMode mode) =>
        string.Join(
            "|",
            BuildReplayTimelineKey(
                activitySourceMessages is { Count: > 0 }
                    ? activitySourceMessages.Select(message => new ChatMessageViewModel(message)).ToList()
                    : messages.ToList(),
                showToolCalls,
                mode),
            includeReset ? "reset" : "noreset",
            System.Globalization.CultureInfo.CurrentUICulture.Name);

    private static string BuildReplayTimelineKey(
        IReadOnlyList<ChatMessageViewModel> timeline,
        bool showToolCalls,
        TimelineProjectionMode mode)
    {
        var builder = new StringBuilder();
        builder.Append(showToolCalls ? '1' : '0');
        builder.Append(mode == TimelineProjectionMode.HighFidelity ? 'H' : 'L');
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

    private static IEnumerable<string> BuildReplayEventsForMessage(
        ChatMessageViewModel message,
        int? responseDurationMs = null)
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
            yield return SerializeStaticAssistantHtml(message, streaming: false, responseDurationMs);
        }
    }

    private static IEnumerable<string> BuildToolReplayEvents(ChatMessageViewModel message)
    {
        var toolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;
        var toolName = string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : message.ToolName;

        if (string.Equals(toolName, "ask_plan_clarification", StringComparison.OrdinalIgnoreCase)
            && TryReplayPlanClarify(message, out var clarifyEvent))
        {
            yield return clarifyEvent;
            yield break;
        }

        if (string.Equals(toolName, "publish_plan", StringComparison.OrdinalIgnoreCase)
            && TryReplayPlanReady(message, out var planReadyEvent))
        {
            yield return planReadyEvent;
            yield break;
        }

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

    private static bool TryReplayPlanClarify(ChatMessageViewModel message, out string eventJson)
    {
        eventJson = string.Empty;
        if (!TryParseToolArguments(message.ToolArgumentsText, out var root))
        {
            return false;
        }

        if (!root.TryGetProperty("questions", out var questionsEl) || questionsEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var clarification = new PlanClarification
        {
            RequestId = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId,
            AllowFreeText = !root.TryGetProperty("allow_free_text", out var freeEl)
                || freeEl.ValueKind != JsonValueKind.False
        };

        foreach (var item in questionsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var question = new PlanClarificationQuestion
            {
                Id = item.TryGetProperty("id", out var idEl) ? idEl.GetString()?.Trim() ?? "" : "",
                Prompt = item.TryGetProperty("prompt", out var promptEl) ? promptEl.GetString()?.Trim() ?? "" : "",
                AllowMultiple = item.TryGetProperty("allow_multiple", out var multiEl)
                    && multiEl.ValueKind == JsonValueKind.True
            };
            if (item.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var optionEl in optionsEl.EnumerateArray())
                {
                    if (optionEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var optionId = optionEl.TryGetProperty("id", out var oid) ? oid.GetString()?.Trim() : null;
                    var label = optionEl.TryGetProperty("label", out var olabel) ? olabel.GetString()?.Trim() : null;
                    if (string.IsNullOrWhiteSpace(optionId) || string.IsNullOrWhiteSpace(label))
                    {
                        continue;
                    }

                    question.Options.Add(new PlanClarificationOption { Id = optionId, Label = label });
                }
            }

            if (!string.IsNullOrWhiteSpace(question.Id) && !string.IsNullOrWhiteSpace(question.Prompt) && question.Options.Count >= 2)
            {
                clarification.Questions.Add(question);
            }
        }

        if (clarification.Questions.Count == 0)
        {
            return false;
        }

        eventJson = SerializePlanClarifyRequest(clarification, resolved: !message.IsToolRunning);
        return true;
    }

    private static bool TryReplayPlanReady(ChatMessageViewModel message, out string eventJson)
    {
        eventJson = string.Empty;
        if (!TryParseToolArguments(message.ToolArgumentsText, out var root))
        {
            return false;
        }

        var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var overview = root.TryGetProperty("overview", out var overviewEl) ? overviewEl.GetString() : null;
        var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var markdown = PlanDocumentParser.ComposeMarkdown(title, overview, body);
        var todos = new List<PlanTodoItem>();
        if (root.TryGetProperty("todos", out var todosEl) && todosEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in todosEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString()?.Trim() : null;
                var content = item.TryGetProperty("content", out var contentEl) ? contentEl.GetString()?.Trim() : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(content))
                {
                    todos.Add(new PlanTodoItem { Id = id, Content = content });
                }
            }
        }

        if (todos.Count == 0)
        {
            todos.AddRange(PlanDocumentParser.ParseTodos(markdown));
        }

        var run = new PlanRun
        {
            Id = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId,
            SessionId = "",
            Title = title,
            Overview = overview,
            PlanMarkdown = markdown,
            Todos = todos
        };
        eventJson = SerializePlanReady(run);
        return true;
    }

    private static bool TryParseToolArguments(string? text, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(text) || text == "…")
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
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
        bool? hasOlderMessages = null,
        int? renderGeneration = null,
        bool replayComplete = false)
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

            if (replayComplete)
            {
                writer.WriteBoolean("replayComplete", true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
