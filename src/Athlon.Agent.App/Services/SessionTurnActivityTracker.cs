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
    private string? _liveNarration;
    private DateTime _segmentStartedUtc = DateTime.UtcNow;

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
        _liveNarration = null;
        _segmentStartedUtc = DateTime.UtcNow;
    }

    public void Clear() => BeginTurn();

    /// <summary>True when the live activity fold still has unsealed content.</summary>
    public bool HasSegmentContent =>
        _turnMessages.Count > 0 || _hasActiveThought || !string.IsNullOrWhiteSpace(_liveNarration);

    public void FinishPendingThought() => FinishActiveThought();

    /// <summary>
    /// Provisional intermediate assistant text shown inside the activity fold while streaming,
    /// so it never flashes as a standalone bubble outside the fold.
    /// </summary>
    public void SetLiveNarration(string text)
    {
        var trimmed = text.TrimEnd();
        _liveNarration = trimmed.Length == 0 ? null : trimmed;
    }

    public void ClearLiveNarration() => _liveNarration = null;

    public void AddNarration(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        FinishActiveThought();
        _liveNarration = null;
        _turnMessages.Add(new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.Assistant, trimmed)));
    }

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
        if (_turnMessages.Count == 0
            && (!_hasActiveThought || _activeThought.Length == 0)
            && string.IsNullOrWhiteSpace(_liveNarration))
        {
            return null;
        }

        List<ChatMessageViewModel>? provisional = null;
        if (_hasActiveThought && _activeThought.Length > 0)
        {
            provisional = new List<ChatMessageViewModel>(_turnMessages)
            {
                new(ChatMessage.Create(
                    MessageRole.Assistant,
                    string.Empty,
                    reasoningContent: _activeThought.ToString()))
            };
        }

        if (!string.IsNullOrWhiteSpace(_liveNarration))
        {
            provisional ??= new List<ChatMessageViewModel>(_turnMessages);
            provisional.Add(new ChatMessageViewModel(
                ChatMessage.Create(MessageRole.Assistant, _liveNarration)));
        }

        var built = TurnActivitySummaryBuilder.Build(provisional ?? _turnMessages);
        return built is null ? null : built with { DurationMs = GetSegmentDurationMs() };
    }

    private int GetSegmentDurationMs()
    {
        var ms = (int)Math.Round((DateTime.UtcNow - _segmentStartedUtc).TotalMilliseconds);
        return Math.Max(0, ms);
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
