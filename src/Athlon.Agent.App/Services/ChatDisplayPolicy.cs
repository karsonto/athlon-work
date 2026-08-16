using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

internal static class ChatDisplayPolicy
{
    public static bool ShouldIncludeToolMessage(bool showToolCalls, ChatMessage message)
    {
        if (message.Role != MessageRole.Tool)
        {
            return true;
        }

        if (IsActivityToolMessage(message))
        {
            return false;
        }

        return showToolCalls;
    }

    public static bool ShouldDisplayCompactionCheckpoint(ChatMessage message) =>
        message.Role == MessageRole.Compaction
        && CompactionAuditDisplay.Parse(message.Content).Strategy == CompactionStrategy.ManualCompact;

    public static bool ShouldDisplayCompactionCheckpoint(ChatMessageViewModel vm)
    {
        if (!vm.IsCompaction)
        {
            return false;
        }

        if (string.Equals(
                vm.MessageId,
                ChatMessageViewModel.PendingManualCompactionMessageId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return CompactionAuditDisplay.Parse(vm.Content).Strategy == CompactionStrategy.ManualCompact;
    }

    public static bool ShouldIncludeToolViewModel(bool showToolCalls, ChatMessageViewModel vm)
    {
        if (vm.IsCompaction)
        {
            return ShouldDisplayCompactionCheckpoint(vm);
        }

        if (!vm.IsTool)
        {
            return true;
        }

        if (vm.ToolApprovalState == ToolApprovalState.Pending)
        {
            return true;
        }

        if (TurnActivityClassifier.IsActivityTool(vm.ToolName))
        {
            // Keep approval cards visible; completed activity tools fold into TURN_ACTIVITY.
            return vm.ToolApprovalState is ToolApprovalState.Pending or ToolApprovalState.Denied;
        }

        return showToolCalls;
    }

    private static bool IsActivityToolMessage(ChatMessage message)
    {
        ToolMessageDisplayParser.ParseToolContent(
            message.Content,
            out _,
            out var toolName,
            out _,
            out _,
            out _,
            out _,
            out _);
        return TurnActivityClassifier.IsActivityTool(toolName);
    }

    public static bool IsToolStreamEvent(AgentStreamEvent streamEvent) =>
        streamEvent is AgentStreamEvent.ToolCallStart
            or AgentStreamEvent.ToolCallArgs
            or AgentStreamEvent.ToolCallEnd
            or AgentStreamEvent.ToolCallResult
            or AgentStreamEvent.ToolCallOutput;
}
