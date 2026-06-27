using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Maps raw streaming tool-call events to UI-safe display events (summarized for large tools).
/// </summary>
internal sealed class ToolCallArgsDisplayCoordinator
{
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastRawArgs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fileWritePathAnnounced = new(StringComparer.Ordinal);

    public void Reset()
    {
        _toolNames.Clear();
        _lastRawArgs.Clear();
        _fileWritePathAnnounced.Clear();
    }

    public IReadOnlyList<AgentStreamEvent> MapForUi(AgentStreamEvent streamEvent)
    {
        switch (streamEvent)
        {
            case AgentStreamEvent.RunStarted:
                Reset();
                return [streamEvent];
            case AgentStreamEvent.ToolCallStart(var toolCallId, var toolName, var index):
                TrackToolStart(toolCallId, toolName);
                return [streamEvent];
            case AgentStreamEvent.ToolCallArgs(var toolCallId, var rawJson):
                return MapToolCallArgs(toolCallId, rawJson);
            case AgentStreamEvent.ToolCallEnd(var toolCallId):
                return MapToolCallEnd(toolCallId);
            default:
                return [streamEvent];
        }
    }

    private void TrackToolStart(string toolCallId, string toolName)
    {
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            _toolNames[toolCallId] = toolName;
        }
    }

    private IReadOnlyList<AgentStreamEvent> MapToolCallArgs(string toolCallId, string rawJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return [new AgentStreamEvent.ToolCallArgs(toolCallId, rawJson)];
        }

        _lastRawArgs[toolCallId] = rawJson;
        if (!TryGetToolName(toolCallId, out var toolName) || !FileWriteToolArgumentsDisplay.IsFileWrite(toolName))
        {
            return [new AgentStreamEvent.ToolCallArgs(toolCallId, rawJson)];
        }

        if (_fileWritePathAnnounced.Contains(toolCallId))
        {
            return [];
        }

        if (!ToolCallStreamingJsonHelper.TryExtractStringProperty(rawJson, ToolPathNormalizer.PathArgumentName, out var path)
            || string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        _fileWritePathAnnounced.Add(toolCallId);
        return [new AgentStreamEvent.ToolCallArgs(toolCallId, FileWriteToolArgumentsDisplay.FormatStreaming(path))];
    }

    private IReadOnlyList<AgentStreamEvent> MapToolCallEnd(string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId)
            || !TryGetToolName(toolCallId, out var toolName)
            || !FileWriteToolArgumentsDisplay.IsFileWrite(toolName))
        {
            return [new AgentStreamEvent.ToolCallEnd(toolCallId)];
        }

        _lastRawArgs.TryGetValue(toolCallId, out var rawJson);
        var summary = FileWriteToolArgumentsDisplay.FormatFinalFromRawJson(rawJson);
        CleanupToolCall(toolCallId);
        return
        [
            new AgentStreamEvent.ToolCallArgs(toolCallId, summary),
            new AgentStreamEvent.ToolCallEnd(toolCallId)
        ];
    }

    private void CleanupToolCall(string toolCallId)
    {
        _toolNames.Remove(toolCallId);
        _lastRawArgs.Remove(toolCallId);
        _fileWritePathAnnounced.Remove(toolCallId);
    }

    private bool TryGetToolName(string toolCallId, out string toolName)
    {
        if (_toolNames.TryGetValue(toolCallId, out toolName!))
        {
            return true;
        }

        toolName = string.Empty;
        return false;
    }
}
