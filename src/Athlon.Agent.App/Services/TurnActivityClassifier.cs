using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>Classifies workspace activity tools that fold into the turn-activity summary bubble.</summary>
internal static class TurnActivityClassifier
{
    private static readonly HashSet<string> ActivityTools = new(StringComparer.Ordinal)
    {
        "file_edit",
        "file_write",
        "apply_patch",
        "file_read",
        "grep_files",
        "glob_files",
        "file_list",
        "execute_command"
    };

    public static bool IsActivityTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && ActivityTools.Contains(toolName);

    public static bool IsActivityToolStreamEvent(AgentStreamEvent streamEvent, Func<string, string?>? resolveToolName = null) =>
        streamEvent switch
        {
            AgentStreamEvent.ToolCallStart(_, var toolName, _) => IsActivityTool(toolName),
            AgentStreamEvent.ToolCallArgs(var toolCallId, _) =>
                IsActivityTool(resolveToolName?.Invoke(toolCallId)),
            AgentStreamEvent.ToolCallEnd(var toolCallId) =>
                IsActivityTool(resolveToolName?.Invoke(toolCallId)),
            AgentStreamEvent.ToolCallResult(var toolCallId, var content, _) =>
                IsActivityTool(resolveToolName?.Invoke(toolCallId) ?? TryParseToolName(content)),
            AgentStreamEvent.ToolCallOutput(var toolCallId, _) =>
                IsActivityTool(resolveToolName?.Invoke(toolCallId)),
            _ => false
        };

    private static string? TryParseToolName(string content)
    {
        ToolMessageDisplayParser.ParseToolContent(
            content,
            out _,
            out var toolName,
            out _,
            out _,
            out _,
            out _,
            out _);
        return toolName;
    }
}
