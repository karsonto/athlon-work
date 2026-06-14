using System.Collections.ObjectModel;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services.Streaming;

public sealed class SessionStreamingUiContext
{
    private readonly Dictionary<string, ChatMessageViewModel> _assistantBubbles = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ChatMessageViewModel> _toolBubblesByIndex = new();
    private readonly Dictionary<string, int> _toolCallIdToIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatMessageViewModel> _outputToolBubbles = new(StringComparer.Ordinal);

    public Action RequestScroll { get; set; } = () => { };

    public Action RequestScrollImmediate { get; set; } = () => { };

    public ChatMessageViewModel? ActiveAssistantBubble =>
        _assistantBubbles.Values.LastOrDefault();

    public IReadOnlyDictionary<int, ChatMessageViewModel> ToolBubblesByIndex => _toolBubblesByIndex;

    public void Process(AgentStreamEvent streamEvent, ObservableCollection<ChatMessageViewModel> messages)
    {
        switch (streamEvent)
        {
            case AgentStreamEvent.RunStarted:
                Reset();
                break;
            case AgentStreamEvent.TextMessageStart(var messageId, _):
                EnsureAssistantBubble(messageId, messages);
                break;
            case AgentStreamEvent.TextMessageContent(var messageId, var delta):
                EnsureAssistantBubble(messageId, messages);
                var textBubble = GetAssistantBubble(messageId);
                textBubble?.AppendStreamingToken(delta);
                textBubble?.FlushStreamingContent();
                RequestScroll();
                break;
            case AgentStreamEvent.TextMessageEnd(var messageId):
                ReleaseAssistantBubble(messageId, messages);
                break;
            case AgentStreamEvent.ReasoningMessageStart(var messageId, _):
                EnsureAssistantBubble(messageId, messages);
                break;
            case AgentStreamEvent.ReasoningMessageContent(var messageId, var delta):
                EnsureAssistantBubble(messageId, messages);
                var reasoningBubble = GetAssistantBubble(messageId);
                reasoningBubble?.AppendStreamingReasoningToken(delta);
                reasoningBubble?.FlushStreamingContent();
                RequestScroll();
                break;
            case AgentStreamEvent.ReasoningMessageEnd(var messageId):
                ReleaseAssistantBubble(messageId, messages);
                break;
            case AgentStreamEvent.ToolCallStart(var toolCallId, var toolName, var index):
                if (index is int toolIndex)
                {
                    _toolCallIdToIndex[toolCallId] = toolIndex;
                    EnsureToolBubble(toolIndex, toolCallId, toolName, messages);
                }

                break;
            case AgentStreamEvent.ToolCallArgs(var toolCallId, var argsJson):
                if (_toolCallIdToIndex.TryGetValue(toolCallId, out var argsIndex)
                    && _toolBubblesByIndex.TryGetValue(argsIndex, out var toolBubble))
                {
                    toolBubble.UpdateStreamingToolCall(toolCallId, toolBubble.ToolName, argsJson);
                }

                RequestScroll();
                break;
            case AgentStreamEvent.ToolCallEnd(var toolCallId):
                if (_toolCallIdToIndex.TryGetValue(toolCallId, out var endIndex)
                    && _toolBubblesByIndex.TryGetValue(endIndex, out var endedTool))
                {
                    endedTool.PromoteStreamingToolToRunning(
                        new AgentToolCall(
                            toolCallId,
                            string.IsNullOrWhiteSpace(endedTool.ToolName) ? "unknown" : endedTool.ToolName,
                            ToolCallArgumentsParser.ParseJson(endedTool.ToolArgumentsText)));
                    _toolBubblesByIndex.Remove(endIndex);
                    _outputToolBubbles[toolCallId] = endedTool;
                }

                break;
            case AgentStreamEvent.ToolCallResult(var toolCallId, var content, var messageId):
                HandleToolCallResult(toolCallId, content, messageId, messages);
                break;
            case AgentStreamEvent.ToolCallOutput(var toolCallId, var delta):
                HandleToolCallOutput(toolCallId, delta, messages);
                break;
            case AgentStreamEvent.ChatMessageAppended(var message):
                HandleChatMessageAppended(message, messages);
                break;
            case AgentStreamEvent.ClearEmptyAssistantPlaceholder:
                RemoveEmptyActiveAssistantBubble(messages);
                break;
            case AgentStreamEvent.RunFinished:
                break;
        }
    }

    public void Reset()
    {
        _assistantBubbles.Clear();
        _toolBubblesByIndex.Clear();
        _toolCallIdToIndex.Clear();
        _outputToolBubbles.Clear();
    }

    private void HandleChatMessageAppended(ChatMessage message, ObservableCollection<ChatMessageViewModel> messages)
    {
        if (ChatMessageViewModel.IsAssistantToolCallsOnly(message))
        {
            return;
        }

        if (message.Role == MessageRole.User && SummaryMessageBuilder.IsSummaryMessage(message))
        {
            RemoveEmptyActiveAssistantBubble(messages);
            return;
        }

        if (message.Role == MessageRole.Compaction)
        {
            RemoveEmptyActiveAssistantBubble(messages);
            if (!ContainsMessageId(messages, message.Id))
            {
                messages.Add(new ChatMessageViewModel(message));
                RequestScroll();
            }

            return;
        }

        if (message.Role == MessageRole.Tool)
        {
            var toolCallId = AgentRuntime.ExtractToolCallId(message.Content);
            var existing = FindToolMessage(messages, toolCallId);
            if (existing is not null)
            {
                existing.ApplyCompletedTool(message);
                return;
            }

            if (!ContainsMessageId(messages, message.Id))
            {
                messages.Add(new ChatMessageViewModel(message));
                RequestScroll();
            }

            return;
        }

        if (message.Role == MessageRole.Assistant && ActiveAssistantBubble is not null)
        {
            ActiveAssistantBubble.CompleteStreamingAssistant(message);
            _assistantBubbles.Remove(message.Id);
            RequestScrollImmediate();
            return;
        }

        RemoveEmptyActiveAssistantBubble(messages);
        if (!ContainsMessageId(messages, message.Id))
        {
            messages.Add(new ChatMessageViewModel(message));
            RequestScrollImmediate();
        }
    }

    private void HandleToolCallResult(
        string toolCallId,
        string content,
        string messageId,
        ObservableCollection<ChatMessageViewModel> messages)
    {
        _outputToolBubbles.Remove(toolCallId);

        var existing = FindToolMessage(messages, toolCallId);
        var toolMessage = ChatMessage.CreateWithId(messageId, MessageRole.Tool, content);
        if (existing is not null)
        {
            existing.ApplyCompletedTool(toolMessage);
            RemoveToolTracking(existing);
            return;
        }

        var preparing = _toolBubblesByIndex.Values.FirstOrDefault(message =>
            message.ToolCallStatus == ToolCallDisplayStatus.Preparing
            && (string.IsNullOrWhiteSpace(message.ToolCallId)
                || string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal)));
        if (preparing is not null)
        {
            preparing.ApplyCompletedTool(toolMessage);
            RemoveToolTracking(preparing);
            return;
        }

        if (!ContainsMessageId(messages, messageId))
        {
            messages.Add(new ChatMessageViewModel(toolMessage));
            RequestScroll();
        }
    }

    private void HandleToolCallOutput(
        string toolCallId,
        string delta,
        ObservableCollection<ChatMessageViewModel> messages)
    {
        // Fast path: tool bubble tracked by callId (already promoted to Running state)
        if (_outputToolBubbles.TryGetValue(toolCallId, out var tracked))
        {
            tracked.AppendToolOutput(delta);
            return;
        }

        // Fallback: search in the tool-bubbles-by-index (still in Preparing state)
        var preparing = _toolBubblesByIndex.Values.FirstOrDefault(bubble =>
            string.Equals(bubble.ToolCallId, toolCallId, StringComparison.Ordinal));
        if (preparing is not null)
        {
            preparing.AppendToolOutput(delta);
            return;
        }

        // Last resort: search the Messages collection
        var existing = FindToolMessage(messages, toolCallId);
        if (existing is not null)
        {
            existing.AppendToolOutput(delta);
        }
    }

    private void EnsureAssistantBubble(string messageId, ObservableCollection<ChatMessageViewModel> messages)
    {
        if (_assistantBubbles.ContainsKey(messageId))
        {
            return;
        }

        var bubble = ChatMessageViewModel.CreateStreamingAssistant(messageId);
        _assistantBubbles[messageId] = bubble;
        messages.Add(bubble);
        RequestScroll();
    }

    private void ReleaseAssistantBubble(string messageId, ObservableCollection<ChatMessageViewModel> messages)
    {
        if (!_assistantBubbles.TryGetValue(messageId, out var bubble))
        {
            return;
        }

        bubble.FlushStreamingContent();
        if (string.IsNullOrWhiteSpace(bubble.Content) && string.IsNullOrWhiteSpace(bubble.ReasoningContent))
        {
            messages.Remove(bubble);
        }
        else
        {
            bubble.SealStreamingDisplay();
        }

        _assistantBubbles.Remove(messageId);
    }

    private void RemoveEmptyActiveAssistantBubble(ObservableCollection<ChatMessageViewModel> messages)
    {
        foreach (var entry in _assistantBubbles.ToList())
        {
            if (string.IsNullOrWhiteSpace(entry.Value.Content)
                && string.IsNullOrWhiteSpace(entry.Value.ReasoningContent))
            {
                messages.Remove(entry.Value);
                _assistantBubbles.Remove(entry.Key);
            }
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

    private ChatMessageViewModel? GetAssistantBubble(string messageId) =>
        _assistantBubbles.GetValueOrDefault(messageId);

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

    private static bool ContainsMessageId(ObservableCollection<ChatMessageViewModel> messages, string messageId) =>
        !string.IsNullOrWhiteSpace(messageId)
        && messages.Any(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal));

    private void RemoveToolTracking(ChatMessageViewModel message)
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
