using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Controls how tool calls are projected into the chat timeline during replay.
/// Live streaming uses <see cref="LiveFold"/>; hydrate/switch/restart uses <see cref="HighFidelity"/>.
/// </summary>
internal enum TimelineProjectionMode
{
    LiveFold,
    HighFidelity
}

/// <summary>
/// Builds turn segments from transcript messages for AG-UI replay projection.
/// </summary>
internal static class ChatTimelineProjector
{
    internal sealed record TurnSegment(
        IReadOnlyList<ChatMessageViewModel> UserMessages,
        IReadOnlyList<ChatMessageViewModel> ActivitySegment,
        IReadOnlyList<ChatMessageViewModel> ToolMessages,
        IReadOnlyList<ChatMessageViewModel> AssistantMessages,
        ChatMessageViewModel? CompactionMessage,
        DateTimeOffset? TurnUserCreatedAt);

    public static IReadOnlyList<TurnSegment> BuildSegments(
        IReadOnlyList<ChatMessageViewModel> timeline,
        bool showToolCalls,
        TimelineProjectionMode mode = TimelineProjectionMode.HighFidelity)
    {
        var segments = new List<TurnSegment>();
        var activitySegment = new List<ChatMessageViewModel>();
        var pendingToolMessages = new List<ChatMessageViewModel>();
        var pendingAssistants = new List<ChatMessageViewModel>();
        var finalAssistantMessageIds = FindFinalAssistantMessageIds(timeline, mode);
        DateTimeOffset? turnUserCreatedAt = null;

        void FlushTurnIntermediate()
        {
            if (activitySegment.Count > 0
                || pendingToolMessages.Count > 0
                || pendingAssistants.Count > 0)
            {
                segments.Add(new TurnSegment(
                    UserMessages: Array.Empty<ChatMessageViewModel>(),
                    ActivitySegment: activitySegment.ToArray(),
                    ToolMessages: pendingToolMessages.ToArray(),
                    AssistantMessages: pendingAssistants.ToArray(),
                    CompactionMessage: null,
                    TurnUserCreatedAt: turnUserCreatedAt));
            }

            activitySegment.Clear();
            pendingToolMessages.Clear();
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
                turnUserCreatedAt = message.CreatedAtUtc;
                segments.Add(new TurnSegment(
                    UserMessages: [message],
                    ActivitySegment: Array.Empty<ChatMessageViewModel>(),
                    ToolMessages: Array.Empty<ChatMessageViewModel>(),
                    AssistantMessages: Array.Empty<ChatMessageViewModel>(),
                    CompactionMessage: null,
                    TurnUserCreatedAt: turnUserCreatedAt));
                continue;
            }

            if (message.IsCompaction)
            {
                if (ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(message))
                {
                    FlushTurnIntermediate();
                    turnUserCreatedAt = null;
                    segments.Add(new TurnSegment(
                        UserMessages: Array.Empty<ChatMessageViewModel>(),
                        ActivitySegment: Array.Empty<ChatMessageViewModel>(),
                        ToolMessages: Array.Empty<ChatMessageViewModel>(),
                        AssistantMessages: Array.Empty<ChatMessageViewModel>(),
                        CompactionMessage: message,
                        TurnUserCreatedAt: null));
                }

                continue;
            }

            if (message.IsTool)
            {
                if (ShouldEmitToolCard(showToolCalls, mode, message))
                {
                    pendingToolMessages.Add(message);
                    continue;
                }

                if (mode == TimelineProjectionMode.LiveFold
                    && TurnActivityClassifier.IsActivityTool(message.ToolName))
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
                if (finalAssistantMessageIds.Contains(message.MessageId))
                {
                    pendingAssistants.Add(message);
                }
                else
                {
                    activitySegment.Add(message);
                }
            }
        }

        FlushTurnIntermediate();
        return segments;
    }

    internal static bool ShouldEmitToolCard(
        bool showToolCalls,
        TimelineProjectionMode mode,
        ChatMessageViewModel message)
    {
        if (!showToolCalls)
        {
            return false;
        }

        if (mode == TimelineProjectionMode.HighFidelity)
        {
            return ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls: true, message)
                || TurnActivityClassifier.IsActivityTool(message.ToolName);
        }

        return ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls, message);
    }

    internal static HashSet<string> FindFinalAssistantMessageIds(
        IReadOnlyList<ChatMessageViewModel> timeline,
        TimelineProjectionMode mode)
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

            if (message.IsTool)
            {
                if (mode == TimelineProjectionMode.HighFidelity)
                {
                    turnHasActivity = true;
                }
                else if (TurnActivityClassifier.IsActivityTool(message.ToolName))
                {
                    turnHasActivity = true;
                }
            }
            else if (message.HasReasoning)
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
}
