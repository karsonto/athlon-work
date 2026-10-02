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

/// <summary>Rebuilds a timeline from stored messages.</summary>
internal static partial class ChatEventSerializer
{
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
                        BlockEvents: Array.Empty<string>(),
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
                    BlockEvents: Array.Empty<string>(),
                    CompactionEvent: SerializeCompactionCheckpoint(compaction) is { } compactionEvent
                        ? WithSeq(compactionEvent, TimelineOrderPolicy.Compaction(turnIndex))
                        : null,
                    TurnIndex: turnIndex));
                continue;
            }

            // A turn without a leading user message (mid-turn tail page) still needs its own band.
            turnIndex++;
            var currentTurn = turnIndex;

            // Folds, tool cards, per-edit cards and assistant replies are numbered together in
            // transcript order, so the rebuilt timeline keeps the exact interleaving the turn
            // streamed with. A succeeded file edit renders as its own single-file card at its slot
            // instead of a tool card, so file modifications appear where they happened.
            var blockEvents = new List<string>();
            var blockOrdinal = 0;
            var activityBlockIndex = 0;

            // The response duration is a property of the turn, shown on its last assistant reply
            // only, so find it up front.
            var lastAssistant = segment.Blocks
                .OfType<ChatTimelineProjector.ContentBlock>()
                .LastOrDefault(content =>
                    !content.Message.IsTool
                    && !string.IsNullOrWhiteSpace(content.Message.Content))
                ?.Message;

            var browserScreenshots = new List<ImageAttachment>();
            foreach (var block in segment.Blocks)
            {
                var seq = TimelineOrderPolicy.Block(currentTurn, blockOrdinal++);
                switch (block)
                {
                    case ChatTimelineProjector.ActivityBlock activityBlock:
                        foreach (var activityMessage in activityBlock.Messages)
                        {
                            BrowserScreenshotMarkdown.Collect(activityMessage.ImageAttachments, browserScreenshots);
                        }

                        var activity = TurnActivitySummaryBuilder.Build(activityBlock.Messages);
                        if (activity is { HasContent: true })
                        {
                            blockEvents.Add(SerializeTurnActivity(
                                activity,
                                seq: seq,
                                turnAnchorId: segment.TurnAnchorId,
                                activityBlockIndex: activityBlockIndex));
                            // The index counts emitted folds, matching the live controller's counter,
                            // so a live upsert and this replayed fold resolve to one entry.
                            activityBlockIndex++;
                        }

                        break;

                    case ChatTimelineProjector.ContentBlock { Message.IsTool: true } edit
                        when ChatTimelineProjector.IsSucceededFileEdit(edit.Message):
                        BrowserScreenshotMarkdown.Collect(edit.Message.ImageAttachments, browserScreenshots);
                        var editEvent = SerializeEditCard(edit.Message, seq);
                        if (editEvent is not null)
                        {
                            blockEvents.Add(editEvent);
                        }

                        break;

                    case ChatTimelineProjector.ContentBlock contentBlock:
                        var content = contentBlock.Message;
                        if (content.IsTool)
                        {
                            BrowserScreenshotMarkdown.Collect(content.ImageAttachments, browserScreenshots);
                            blockEvents.AddRange(BuildReplayEventsForMessage(content, seq: seq));
                            break;
                        }

                        var durationMs = ReferenceEquals(content, lastAssistant)
                            && segment.TurnUserCreatedAt is { } startedAt
                            ? ComputeResponseDurationMs(startedAt, content.CreatedAtUtc)
                            : null;
                        blockEvents.AddRange(BuildReplayEventsForMessage(
                            content,
                            durationMs,
                            seq,
                            browserScreenshots));
                        break;
                }
            }

            if (blockEvents.Count > 0
                // A turn whose only tool was publish_plan has no other events, but still owns the
                // plan card's slot. Dropping the segment here would drop the card on replay.
                || segment.HasPlanPublish)
            {
                segments.Add(new ReplayTurnSegment(
                    UserEvents: Array.Empty<string>(),
                    BlockEvents: blockEvents.ToArray(),
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
            events.AddRange(segment.BlockEvents);

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
        IReadOnlyList<string> BlockEvents,
        string? CompactionEvent,
        /// <summary>Index of this segment in the replay, paired with <see cref="TimelineOrderPolicy"/>.</summary>
        long TurnIndex = -1,
        /// <summary>The slot for a plan card when this turn called <c>publish_plan</c>.</summary>
        long? PlanSeq = null);

    private static IEnumerable<string> BuildReplayEventsForMessage(
        ChatMessageViewModel message,
        int? responseDurationMs = null,
        long? seq = null,
        IReadOnlyList<ImageAttachment>? browserScreenshots = null)
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
            yield return SerializeStaticAssistantHtml(
                message,
                streaming: false,
                responseDurationMs,
                seq,
                browserScreenshots);
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
        var images = ResolveTimelineImages(message.ImageAttachments);
        if (!string.IsNullOrWhiteSpace(detail) || images.Count > 0)
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
                html = RenderToolResultHtml(message, detail),
                images
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
}
