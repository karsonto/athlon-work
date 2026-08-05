using System.Text;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>Live accumulator for the current turn's Cursor-style activity summary.</summary>
public sealed class SessionTurnActivityTracker
{
    private readonly Dictionary<string, string> _toolCallIdToName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolCallIdToArgs = new(StringComparer.Ordinal);
    private readonly List<ChatMessageViewModel> _turnMessages = new();
    private readonly StringBuilder _activeThought = new();
    private bool _hasActiveThought;

    public void BeginTurn()
    {
        _toolCallIdToName.Clear();
        _toolCallIdToArgs.Clear();
        BeginSegment();
    }

    /// <summary>Clears accumulated segment activity after sealing a bubble above model text.</summary>
    public void BeginSegment()
    {
        _turnMessages.Clear();
        _activeThought.Clear();
        _hasActiveThought = false;
    }

    public void Clear() => BeginTurn();

    public void FinishPendingThought() => FinishActiveThought();

    public string? ResolveToolName(string toolCallId) =>
        _toolCallIdToName.TryGetValue(toolCallId, out var name) ? name : null;

    public void Process(AgentStreamEvent streamEvent)
    {
        switch (streamEvent)
        {
            case AgentStreamEvent.ReasoningMessageStart:
                FinishActiveThought();
                _hasActiveThought = true;
                _activeThought.Clear();
                break;
            case AgentStreamEvent.ReasoningMessageContent(_, var delta):
                if (!_hasActiveThought)
                {
                    _hasActiveThought = true;
                    _activeThought.Clear();
                }

                _activeThought.Append(delta);
                break;
            case AgentStreamEvent.ReasoningMessageEnd:
                FinishActiveThought();
                break;
            case AgentStreamEvent.ToolCallStart(var toolCallId, var toolName, _):
                FinishActiveThought();
                _toolCallIdToName[toolCallId] = toolName;
                EnsurePendingTool(toolCallId, toolName);
                break;
            case AgentStreamEvent.ToolCallArgs(var toolCallId, var argsJson):
                _toolCallIdToArgs[toolCallId] = argsJson;
                UpdatePendingToolArgs(toolCallId, argsJson);
                break;
            case AgentStreamEvent.ToolCallEnd(var toolCallId):
                PromotePendingToolToRunning(toolCallId);
                break;
            case AgentStreamEvent.ToolCallResult(var toolCallId, var content, _):
                FinishActiveThought();
                HandleResult(toolCallId, content);
                _toolCallIdToName.Remove(toolCallId);
                _toolCallIdToArgs.Remove(toolCallId);
                break;
        }
    }

    public TurnActivitySummary? Snapshot()
    {
        if (_turnMessages.Count == 0 && (!_hasActiveThought || _activeThought.Length == 0))
        {
            return null;
        }

        if (!_hasActiveThought || _activeThought.Length == 0)
        {
            return TurnActivitySummaryBuilder.Build(_turnMessages);
        }

        // Include in-progress thought without mutating the committed list.
        var provisional = new List<ChatMessageViewModel>(_turnMessages)
        {
            new(ChatMessage.Create(
                MessageRole.Assistant,
                string.Empty,
                reasoningContent: _activeThought.ToString()))
        };
        return TurnActivitySummaryBuilder.Build(provisional);
    }

    private void FinishActiveThought()
    {
        if (!_hasActiveThought)
        {
            return;
        }

        var text = _activeThought.ToString().Trim();
        _hasActiveThought = false;
        _activeThought.Clear();
        if (text.Length == 0)
        {
            return;
        }

        _turnMessages.Add(new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.Assistant, string.Empty, reasoningContent: text)));
    }

    private void EnsurePendingTool(string toolCallId, string toolName)
    {
        if (!TurnActivityClassifier.IsActivityTool(toolName)
            || FindToolMessage(toolCallId) is not null)
        {
            return;
        }

        var pending = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall(toolCallId, toolName, ToolCallArguments.Empty));
        pending.ToolCallStatus = ToolCallDisplayStatus.Preparing;
        pending.IsToolRunning = false;
        if (_toolCallIdToArgs.TryGetValue(toolCallId, out var bufferedArgs)
            && !string.IsNullOrWhiteSpace(bufferedArgs))
        {
            pending.ToolArgumentsText = FormatArgsForDisplay(bufferedArgs, toolName);
        }

        _turnMessages.Add(pending);
    }

    private void UpdatePendingToolArgs(string toolCallId, string argsJson)
    {
        var pending = FindToolMessage(toolCallId);
        if (pending is null
            || pending.ToolCallStatus is not (ToolCallDisplayStatus.Preparing or ToolCallDisplayStatus.Running))
        {
            return;
        }

        _toolCallIdToName.TryGetValue(toolCallId, out var toolName);
        pending.ToolArgumentsText = FormatArgsForDisplay(argsJson, toolName ?? pending.ToolName);
    }

    private void PromotePendingToolToRunning(string toolCallId)
    {
        var pending = FindToolMessage(toolCallId);
        if (pending is null)
        {
            return;
        }

        if (pending.ToolCallStatus is ToolCallDisplayStatus.Preparing or ToolCallDisplayStatus.Running)
        {
            pending.ToolCallStatus = ToolCallDisplayStatus.Running;
            pending.IsToolRunning = true;
            pending.IsToolArgumentsStreaming = false;
        }
    }

    private void HandleResult(string toolCallId, string content)
    {
        _toolCallIdToName.TryGetValue(toolCallId, out var toolName);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            ToolMessageDisplayParser.ParseToolContent(
                content,
                out _,
                out toolName,
                out _,
                out _,
                out _,
                out _,
                out _);
        }

        RemoveToolMessage(toolCallId);

        if (!TurnActivityClassifier.IsActivityTool(toolName))
        {
            return;
        }

        var message = new ChatMessageViewModel(ChatMessage.Create(MessageRole.Tool, content));
        if (string.IsNullOrWhiteSpace(message.ToolArgumentsText)
            && _toolCallIdToArgs.TryGetValue(toolCallId, out var rawArgs)
            && !string.IsNullOrWhiteSpace(rawArgs))
        {
            message.ToolArgumentsText = FormatArgsForDisplay(rawArgs, toolName);
        }

        // Successful edits belong in FILES_CHANGED, not the activity list.
        if (TurnActivitySummaryBuilder.EditTools.Contains(toolName)
            && message.ToolCallStatus == ToolCallDisplayStatus.Succeeded)
        {
            return;
        }

        _turnMessages.Add(message);
    }

    private ChatMessageViewModel? FindToolMessage(string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        return _turnMessages.LastOrDefault(message =>
            message.IsTool && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));
    }

    private void RemoveToolMessage(string toolCallId)
    {
        for (var i = _turnMessages.Count - 1; i >= 0; i--)
        {
            var message = _turnMessages[i];
            if (message.IsTool && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal))
            {
                _turnMessages.RemoveAt(i);
                return;
            }
        }
    }

    private static string FormatArgsForDisplay(string argsJson, string? toolName)
    {
        try
        {
            var parsed = ToolCallArgumentsParser.ParseJson(argsJson);
            return ToolMessageDisplayParser.FormatArgumentsFull(parsed, toolName);
        }
        catch
        {
            return argsJson;
        }
    }
}
