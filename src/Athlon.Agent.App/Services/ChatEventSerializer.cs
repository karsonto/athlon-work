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

    public static string SerializeTurnActivity(
        TurnActivitySummary summary,
        bool upsert = false,
        long? seq = null,
        string? turnAnchorId = null)
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
            seq,
            entryId = turnAnchorId is null ? null : "activity:" + turnAnchorId,
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

    public static string SerializeFilesChanged(
        IReadOnlyList<ModifiedFileViewModel> files,
        bool upsert = false,
        long? seq = null,
        string? turnAnchorId = null,
        string? entryId = null)
    {
        var resolvedEntryId = entryId
            ?? (turnAnchorId is null ? null : "files:" + turnAnchorId);
        if (files.Count == 0)
        {
            return SerializeAgui("FILES_CHANGED", new
            {
                seq,
                entryId = resolvedEntryId,
                upsert,
                files = Array.Empty<object>()
            });
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

        return SerializeAgui("FILES_CHANGED", new
        {
            seq,
            entryId = resolvedEntryId,
            upsert,
            files = payload
        });
    }

    public static string SerializeStaticAssistantHtml(
        ChatMessageViewModel message,
        bool streaming = false,
        int? responseDurationMs = null,
        long? seq = null) =>
        SerializeAgui("STATIC_ASSISTANT_HTML", new
        {
            seq,
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

    public static string SerializePlanReady(PlanRun run, long? seq = null) =>
        SerializeAgui("PLAN_READY", new
        {
            seq,
            runId = run.Id,
            title = run.Title,
            overview = run.Overview,
            planPath = run.PlanPath,
            markdown = run.PlanMarkdown,
            html = MarkdownHtmlRenderer.ToHtmlFragment(run.PlanMarkdown),
            todos = run.Todos.Select(t => new { id = t.Id, content = t.Content }).ToList(),
            built = run.Phase == PlanPhase.Done
                || string.Equals(run.Status, PlanRunStatuses.Approved, StringComparison.OrdinalIgnoreCase)
        });

    /// <summary>
    /// Removes a plan-ready card from the timeline (plan abandoned). Replay never emits it; it
    /// only clears a card the live dispatcher had published from the plan store.
    /// </summary>
    public static string SerializePlanCleared(string runId) =>
        SerializeAgui("PLAN_CLEARED", new { runId });

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

    public static string SerializeAppendCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        SerializeWebMessageCommand(
            "append",
            BuildReplayEvents(
                messages,
                showToolCalls,
                includeReset: false,
                activitySourceMessages: activitySourceMessages));

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
        }, AppJson.Options);

    public static IReadOnlyList<string> BuildReplayEvents(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        bool includeReset = true,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        PlanRun? planRun = null)
    {
        var timeline = activitySourceMessages is { Count: > 0 }
            ? activitySourceMessages
                .Where(message => message.Role is MessageRole.User
                    or MessageRole.Tool
                    or MessageRole.Assistant
                    or MessageRole.Compaction)
                .Select(message => new ChatMessageViewModel(message))
                .ToList()
            : messages.ToList();
        var segments = BuildReplaySegments(timeline, showToolCalls);
        return BuildEventsFromSegments(segments, includeReset, planRun);
    }

    private static IReadOnlyList<ReplayTurnSegment> BuildReplaySegments(
        IReadOnlyList<ChatMessageViewModel> timeline,
        bool showToolCalls)
    {
        var projected = ChatTimelineProjector.BuildSegments(timeline, showToolCalls);
        var segments = new List<ReplayTurnSegment>(projected.Count);
        var turnIndex = -1L;

        foreach (var segment in projected)
        {
            if (segment.UserMessages.Count > 0)
            {
                turnIndex++;
                foreach (var user in segment.UserMessages)
                {
                    segments.Add(new ReplayTurnSegment(
                        UserEvents:
                        [
                            SerializeUserMessage(user, TimelineOrderPolicy.User(turnIndex))
                        ],
                        ActivityEvent: null,
                        ContentEvents: Array.Empty<string>(),
                        CompactionEvent: null,
                        TurnIndex: turnIndex));
                }

                continue;
            }

            if (segment.CompactionMessage is { } compaction)
            {
                turnIndex++;
                segments.Add(new ReplayTurnSegment(
                    UserEvents: Array.Empty<string>(),
                    ActivityEvent: null,
                    ContentEvents: Array.Empty<string>(),
                    CompactionEvent: SerializeCompactionCheckpoint(compaction) is { } compactionEvent
                        ? WithSeq(compactionEvent, TimelineOrderPolicy.Compaction(turnIndex))
                        : null,
                    TurnIndex: turnIndex));
                continue;
            }

            // A turn without a leading user message (mid-turn tail page) still needs its own band.
            turnIndex++;
            var currentTurn = turnIndex;
            string? activityEvent = null;
            if (segment.ActivitySegment.Count > 0)
            {
                var activity = TurnActivitySummaryBuilder.Build(segment.ActivitySegment);
                if (activity is { HasContent: true })
                {
                    activityEvent = SerializeTurnActivity(
                        activity,
                        seq: TimelineOrderPolicy.Activity(currentTurn),
                        turnAnchorId: segment.TurnAnchorId);
                }
            }

            // Number tool cards and assistant replies consecutively in transcript order so the
            // timeline keeps the exact interleaving the turn streamed with. A succeeded file edit
            // renders as its own single-file card at its slot instead of a tool card, so file
            // modifications appear along the timeline where they happened.
            var contentEvents = new List<string>(segment.ContentMessages.Count);
            var contentOrdinal = 0;
            foreach (var content in segment.ContentMessages)
            {
                var seq = TimelineOrderPolicy.Content(currentTurn, contentOrdinal++);
                if (content.IsTool)
                {
                    if (ChatTimelineProjector.IsSucceededFileEdit(content))
                    {
                        var editEvent = SerializeEditCard(content, seq);
                        if (editEvent is not null)
                        {
                            contentEvents.Add(editEvent);
                        }

                        continue;
                    }

                    contentEvents.AddRange(BuildReplayEventsForMessage(content, seq: seq));
                    continue;
                }

                var durationMs = segment.TurnUserCreatedAt is { } startedAt
                    ? ComputeResponseDurationMs(startedAt, content.CreatedAtUtc)
                    : null;
                contentEvents.AddRange(BuildReplayEventsForMessage(content, durationMs, seq));
            }

            if (activityEvent is not null
                || contentEvents.Count > 0
                // A turn whose only tool was publish_plan has no other events, but still owns the
                // plan card's slot. Dropping the segment here would drop the card on replay.
                || segment.HasPlanPublish)
            {
                segments.Add(new ReplayTurnSegment(
                    UserEvents: Array.Empty<string>(),
                    ActivityEvent: activityEvent,
                    ContentEvents: contentEvents.ToArray(),
                    CompactionEvent: null,
                    TurnIndex: currentTurn,
                    PlanSeq: segment.HasPlanPublish ? TimelineOrderPolicy.Plan(currentTurn) : null));
            }
        }

        return segments;
    }

    /// <summary>
    /// One succeeded file edit as its own timeline card, keyed by the tool call id so the live
    /// publish and this replayed card are the same entry. Returns <c>null</c> when the edit has no
    /// resolvable path.
    /// </summary>
    private static string? SerializeEditCard(ChatMessageViewModel message, long seq)
    {
        var files = SessionModifiedFilesTracker.BuildEditCardFiles(message);
        if (files.Count == 0)
        {
            return null;
        }

        return SerializeFilesChanged(files, seq: seq, entryId: EditCardEntryId(message));
    }

    internal static string EditCardEntryId(ChatMessageViewModel message)
    {
        var id = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;
        return "files:edit:" + id;
    }

    /// <summary>
    /// Re-emits an already-serialized event with a <c>seq</c> injected. Used for the compaction
    /// checkpoint, whose serializer is shared with the live path.
    /// </summary>
    private static string WithSeq(string eventJson, long seq)
    {
        using var document = JsonDocument.Parse(eventJson);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var wroteSeq = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("seq"))
                {
                    writer.WriteNumber("seq", seq);
                    wroteSeq = true;
                    continue;
                }

                property.WriteTo(writer);
            }

            if (!wroteSeq)
            {
                writer.WriteNumber("seq", seq);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<string> BuildEventsFromSegments(
        IReadOnlyList<ReplayTurnSegment> segments,
        bool includeReset,
        PlanRun? planRun = null)
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

            events.AddRange(segment.ContentEvents);

            if (segment.CompactionEvent is not null)
            {
                events.Add(segment.CompactionEvent);
            }

            // The plan card is not part of the transcript projection: publish_plan is not rendered
            // as a tool card. A turn that called publish_plan exposes its slot here and the caller
            // hands in the active run, so a live card and a replayed card agree on a position.
            if (planRun is not null && segment.PlanSeq is { } planSeq)
            {
                events.Add(SerializePlanReady(planRun, planSeq));
            }
        }

        return events;
    }

    private sealed record ReplayTurnSegment(
        IReadOnlyList<string> UserEvents,
        string? ActivityEvent,
        IReadOnlyList<string> ContentEvents,
        string? CompactionEvent,
        /// <summary>Index of this segment in the replay, paired with <see cref="TimelineOrderPolicy"/>.</summary>
        long TurnIndex = -1,
        /// <summary>The slot for a plan card when this turn called <c>publish_plan</c>.</summary>
        long? PlanSeq = null);

    private static IEnumerable<string> BuildReplayEventsForMessage(
        ChatMessageViewModel message,
        int? responseDurationMs = null,
        long? seq = null)
    {
        if (message.IsUser)
        {
            yield return SerializeUserMessage(message, seq);
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
            foreach (var evt in BuildToolReplayEvents(message, seq))
            {
                yield return evt;
            }

            yield break;
        }

        // Reasoning is folded into TURN_ACTIVITY; do not emit standalone reasoning bubbles.

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            yield return SerializeStaticAssistantHtml(message, streaming: false, responseDurationMs, seq);
        }
    }

    private static IEnumerable<string> BuildToolReplayEvents(ChatMessageViewModel message, long? seq = null)
    {
        var toolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;
        var toolName = string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : message.ToolName;

        // publish_plan never reaches here: the projector filters it out of every segment and the
        // plan card is emitted from the run handed to BuildEventsFromSegments instead.
        yield return SerializeAgui("TOOL_CALL_START", new { seq, toolCallId, toolCallName = toolName });

        if (!string.IsNullOrWhiteSpace(message.ToolArgumentsText) && message.ToolArgumentsText != "…")
        {
            yield return SerializeAgui("TOOL_CALL_ARGS", new { seq, toolCallId, delta = message.ToolArgumentsText });
        }

        yield return SerializeAgui("TOOL_CALL_END", new
        {
            seq,
            toolCallId,
            status = SerializeToolStatus(message.ToolCallStatus, message.ToolApprovalState)
        });

        if (message.ToolApprovalState == ToolApprovalState.Pending)
        {
            yield return SerializeAgui("TOOL_APPROVAL_REQUEST", new
            {
                seq,
                toolCallId,
                toolName,
                arguments = message.ToolApprovalArgumentsPreview
            });
            yield break;
        }

        if (message.ToolApprovalState is ToolApprovalState.Approved or ToolApprovalState.Denied)
        {
            yield return SerializeAgui("TOOL_APPROVAL_RESOLVED", new
            {
                seq,
                toolCallId,
                approved = message.ToolApprovalState == ToolApprovalState.Approved
            });
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
                seq,
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
