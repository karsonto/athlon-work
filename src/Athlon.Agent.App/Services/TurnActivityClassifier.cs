using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Classifies tools that fold into the single per-turn activity summary.
/// Computer Use keeps full tool cards (screenshots); everything else folds.
/// </summary>
internal static class TurnActivityClassifier
{
    private static readonly HashSet<string> KeepAsToolCard = new(StringComparer.Ordinal)
    {
        "computer_observe",
        "computer_interact",
        "computer_wait"
    };

    public static bool IsActivityTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && !KeepAsToolCard.Contains(toolName);

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
