using System.Collections.ObjectModel;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.Streaming;

public sealed class SessionStreamingUiContext
{
    private readonly StreamingConversionEngine _engine = new();
    private readonly Dictionary<string, ChatMessageViewModel> _assistantBubbles = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ChatMessageViewModel> _toolBubblesByIndex = new();

    public StreamingConversionState State { get; } = new();

    public Action RequestScroll { get; set; } = () => { };

    public Action RequestScrollImmediate { get; set; } = () => { };

    public ChatMessageViewModel? ActiveAssistantBubble =>
        State.ActiveAssistantStreamId is { } streamId
        && _assistantBubbles.TryGetValue(streamId, out var bubble)
            ? bubble
            : null;

    public IReadOnlyDictionary<int, ChatMessageViewModel> ToolBubblesByIndex => _toolBubblesByIndex;

    public void Process(StreamingStreamEvent streamEvent, ObservableCollection<ChatMessageViewModel> messages)
    {
        var result = _engine.Process(State, streamEvent);
        ApplyEffects(result.Effects, messages);
    }

    public void ProcessAll(
        IEnumerable<StreamingStreamEvent> streamEvents,
        ObservableCollection<ChatMessageViewModel> messages)
    {
        foreach (var streamEvent in streamEvents)
        {
            Process(streamEvent, messages);
        }
    }

    public void Reset()
    {
        _engine.Process(State, new StreamingStreamEvent.TurnReset());
        _assistantBubbles.Clear();
        _toolBubblesByIndex.Clear();
    }

    public void FinalizeOpenStreams(ObservableCollection<ChatMessageViewModel> messages)
    {
        Process(new StreamingStreamEvent.TurnFinalize(), messages);
    }

    private void ApplyEffects(
        IReadOnlyList<StreamingUiEffect> effects,
        ObservableCollection<ChatMessageViewModel> messages)
    {
        foreach (var effect in effects)
        {
            ApplyEffect(effect, messages);
        }
    }

    private void ApplyEffect(StreamingUiEffect effect, ObservableCollection<ChatMessageViewModel> messages)
    {
        switch (effect)
        {
            case StreamingUiEffect.EnsureAssistantBubble(var streamId):
                EnsureAssistantBubble(streamId, messages);
                break;
            case StreamingUiEffect.AppendAssistantText(var streamId, var delta):
                GetAssistantBubble(streamId)?.AppendStreamingToken(delta);
                break;
            case StreamingUiEffect.AppendAssistantReasoning(var streamId, var delta):
                GetAssistantBubble(streamId)?.AppendStreamingReasoningToken(delta);
                break;
            case StreamingUiEffect.SealAssistantBubble(var streamId):
                GetAssistantBubble(streamId)?.SealStreamingDisplay();
                break;
            case StreamingUiEffect.ReleaseAssistantBubbleForToolBoundary(var streamId):
                ReleaseAssistantBubbleForToolBoundary(streamId, messages);
                break;
            case StreamingUiEffect.RemoveEmptyAssistantBubble(var streamId):
                RemoveEmptyAssistantBubble(streamId, messages);
                break;
            case StreamingUiEffect.CompleteAssistantBubble(var streamId, var message):
                if (GetAssistantBubble(streamId) is { } completed)
                {
                    completed.CompleteStreamingAssistant(message);
                    _assistantBubbles.Remove(streamId);
                }

                break;
            case StreamingUiEffect.MarkAssistantBubbleCancelled(var streamId):
                if (GetAssistantBubble(streamId) is { } cancelled)
                {
                    cancelled.MarkStreamingCancelled();
                    _assistantBubbles.Remove(streamId);
                }

                break;
            case StreamingUiEffect.EnsureToolBubble(var index, var toolCallId, var name):
                EnsureToolBubble(index, toolCallId, name, messages);
                break;
            case StreamingUiEffect.UpdateToolBubble(var index, var toolCallId, var name, var argumentsJson):
                if (_toolBubblesByIndex.TryGetValue(index, out var toolBubble))
                {
                    toolBubble.UpdateStreamingToolCall(toolCallId, name, argumentsJson);
                }

                break;
            case StreamingUiEffect.PromoteToolBubbleToRunning(var streamIndex, var toolCall):
                PromoteToolBubbleToRunning(streamIndex, toolCall, messages);
                break;
            case StreamingUiEffect.AddPendingToolBubble(var toolCall):
                messages.Add(ChatMessageViewModel.CreatePendingTool(toolCall));
                break;
            case StreamingUiEffect.MarkStreamingToolCancelled(var index):
                if (_toolBubblesByIndex.TryGetValue(index, out var cancelledTool))
                {
                    cancelledTool.MarkStreamingToolCancelled();
                    _toolBubblesByIndex.Remove(index);
                }

                break;
            case StreamingUiEffect.RequestScroll:
                RequestScroll();
                break;
            case StreamingUiEffect.RequestScrollImmediate:
                RequestScrollImmediate();
                break;
        }
    }

    private void EnsureAssistantBubble(string streamId, ObservableCollection<ChatMessageViewModel> messages)
    {
        if (_assistantBubbles.ContainsKey(streamId))
        {
            return;
        }

        var bubble = ChatMessageViewModel.CreateStreamingAssistant();
        _assistantBubbles[streamId] = bubble;
        messages.Add(bubble);
        RequestScroll();
    }

    private void ReleaseAssistantBubbleForToolBoundary(string streamId, ObservableCollection<ChatMessageViewModel> messages)
    {
        if (!_assistantBubbles.TryGetValue(streamId, out var bubble))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(bubble.Content) && string.IsNullOrWhiteSpace(bubble.ReasoningContent))
        {
            messages.Remove(bubble);
        }
        else
        {
            bubble.SealStreamingDisplay();
        }

        _assistantBubbles.Remove(streamId);
    }

    private void RemoveEmptyAssistantBubble(string streamId, ObservableCollection<ChatMessageViewModel> messages)
    {
        if (!_assistantBubbles.TryGetValue(streamId, out var bubble))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(bubble.Content) && string.IsNullOrWhiteSpace(bubble.ReasoningContent))
        {
            messages.Remove(bubble);
            _assistantBubbles.Remove(streamId);
        }
    }

    private void EnsureToolBubble(
        int index,
        string? toolCallId,
        string? name,
        ObservableCollection<ChatMessageViewModel> messages)
    {
        if (_toolBubblesByIndex.ContainsKey(index))
        {
            return;
        }

        var toolBubble = ChatMessageViewModel.CreateStreamingTool(index);
        _toolBubblesByIndex[index] = toolBubble;
        messages.Add(toolBubble);
    }

    private void PromoteToolBubbleToRunning(
        int? streamIndex,
        AgentToolCall toolCall,
        ObservableCollection<ChatMessageViewModel> messages)
    {
        var existing = FindToolMessage(messages, toolCall.Id);
        if (existing is not null)
        {
            if (existing.ToolCallStatus == ToolCallDisplayStatus.Preparing)
            {
                if (existing.StreamToolIndex is int index)
                {
                    _toolBubblesByIndex.Remove(index);
                }

                existing.PromoteStreamingToolToRunning(toolCall);
                RemoveStreamingToolTracking(existing);
            }

            return;
        }

        var preparing = _toolBubblesByIndex.Values.FirstOrDefault(message =>
            message.ToolCallStatus == ToolCallDisplayStatus.Preparing
            && (string.IsNullOrWhiteSpace(message.ToolCallId)
                || string.Equals(message.ToolCallId, toolCall.Id, StringComparison.Ordinal)));
        if (preparing is not null)
        {
            if (preparing.StreamToolIndex is int index)
            {
                _toolBubblesByIndex.Remove(index);
            }

            preparing.PromoteStreamingToolToRunning(toolCall);
            RemoveStreamingToolTracking(preparing);
            return;
        }

        messages.Add(ChatMessageViewModel.CreatePendingTool(toolCall));
    }

    private ChatMessageViewModel? GetAssistantBubble(string streamId) =>
        _assistantBubbles.GetValueOrDefault(streamId);

    private static ChatMessageViewModel? FindToolMessage(
        ObservableCollection<ChatMessageViewModel> messages,
        string? toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        return messages.LastOrDefault(message =>
            message.IsTool && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));
    }

    private void RemoveStreamingToolTracking(ChatMessageViewModel message)
    {
        if (message.StreamToolIndex is int index)
        {
            _toolBubblesByIndex.Remove(index);
            return;
        }

        foreach (var entry in _toolBubblesByIndex.ToList())
        {
            if (ReferenceEquals(entry.Value, message))
            {
                _toolBubblesByIndex.Remove(entry.Key);
                break;
            }
        }
    }
}
