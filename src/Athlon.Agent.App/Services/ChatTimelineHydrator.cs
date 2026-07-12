using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.App.Services;

internal static class ChatTimelineHydrator
{
    public static List<ChatMessageViewModel> BuildDisplayMessages(
        IReadOnlyList<ChatMessage> displayMessages,
        Dictionary<string, ChatMessageViewModel>? viewModelCache = null,
        bool showToolCalls = false,
        bool synthesizeInterruptedToolResults = true)
    {
        var result = new List<ChatMessageViewModel>();
        var answeredToolCallIds = BuildAnsweredToolCallIds(displayMessages);
        var displayedMessageIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in ChatTimelineOrder.OrderForDisplay(displayMessages))
        {
            if (!ChatDisplayPolicy.ShouldIncludeToolMessage(showToolCalls, message))
            {
                continue;
            }

            // Reuse cached ViewModel if available to avoid full FlowDocument rebuild
            if (viewModelCache?.TryGetValue(message.Id, out var cached) == true)
            {
                if (ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls, cached)
                    && displayedMessageIds.Add(cached.MessageId))
                {
                    result.Add(cached);
                }

                continue;
            }

            AddMessageToDisplay(
                result,
                message,
                answeredToolCallIds,
                displayedMessageIds,
                showToolCalls,
                synthesizeInterruptedToolResults);
        }

        return result;
    }

    public static HashSet<string> BuildAnsweredToolCallIds(IReadOnlyList<ChatMessage> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.Tool)
            {
                continue;
            }

            var toolCallId = ExtractToolCallId(message.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                answered.Add(toolCallId);
            }
        }

        return answered;
    }

    public static string? ExtractToolCallId(string content) =>
        AgentRuntime.ExtractToolCallId(content);

    private static void AddMessageToDisplay(
        List<ChatMessageViewModel> messages,
        ChatMessage message,
        HashSet<string> answeredToolCallIds,
        HashSet<string> displayedMessageIds,
        bool showToolCalls,
        bool synthesizeInterruptedToolResults)
    {
        if (ShouldHideMessageFromChat(message) || !displayedMessageIds.Add(message.Id))
        {
            return;
        }

        if (!ChatDisplayPolicy.ShouldIncludeToolMessage(showToolCalls, message))
        {
            return;
        }

        messages.Add(new ChatMessageViewModel(message));

        if (message.Role != MessageRole.Assistant || !showToolCalls || !synthesizeInterruptedToolResults)
        {
            return;
        }

        var pendingCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
        if (pendingCalls is null)
        {
            return;
        }

        foreach (var toolCall in pendingCalls)
        {
            if (answeredToolCallIds.Contains(toolCall.Id))
            {
                continue;
            }

            var orphanResult = AgentRuntime.FormatToolResult(
                toolCall,
                ToolResult.Failure(
                    "工具未完成",
                    "上次对话在工具执行时被中断，或 MCP 超时后子进程未返回。请重启应用并在侧边栏刷新 MCP 后重试。"));
            var orphanMessage = ChatMessage.Create(MessageRole.Tool, orphanResult, message.ParentId);
            if (!displayedMessageIds.Add(orphanMessage.Id))
            {
                continue;
            }

            messages.Add(new ChatMessageViewModel(orphanMessage));
            answeredToolCallIds.Add(toolCall.Id);
        }
    }

    public static bool ShouldHideMessageFromChat(ChatMessage message) =>
        message.Role == MessageRole.User && SummaryMessageBuilder.IsSummaryMessage(message)
        || message.Role == MessageRole.User && SubAgentAutoContinuePrompt.IsAutoContinueMessage(message)
        || ChatMessageViewModel.IsAssistantToolCallsOnly(message);
}
