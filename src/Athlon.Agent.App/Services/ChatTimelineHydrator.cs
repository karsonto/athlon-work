using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.App.Services;

internal static class ChatTimelineHydrator
{
    public static List<ChatMessageViewModel> BuildDisplayMessages(
        IReadOnlyList<ChatMessage> displayMessages,
        Dictionary<string, ChatMessageViewModel>? viewModelCache = null)
    {
        var result = new List<ChatMessageViewModel>();
        var answeredToolCallIds = BuildAnsweredToolCallIds(displayMessages);

        foreach (var message in ChatTimelineOrder.OrderForDisplay(displayMessages))
        {
            // Reuse cached ViewModel if available to avoid full FlowDocument rebuild
            if (viewModelCache?.TryGetValue(message.Id, out var cached) == true)
            {
                // Re-add to result; the ViewModel's bindings will reattach when ListBox item is created
                result.Add(cached);
                continue;
            }

            AddMessageToDisplay(result, message, answeredToolCallIds);
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
        HashSet<string> answeredToolCallIds)
    {
        if (ShouldHideMessageFromChat(message) || ContainsMessageId(messages, message.Id))
        {
            return;
        }

        messages.Add(new ChatMessageViewModel(message));

        if (message.Role != MessageRole.Assistant)
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
            if (ContainsMessageId(messages, orphanMessage.Id))
            {
                continue;
            }

            messages.Add(new ChatMessageViewModel(orphanMessage));
            answeredToolCallIds.Add(toolCall.Id);
        }
    }

    private static bool ContainsMessageId(IReadOnlyList<ChatMessageViewModel> messages, string messageId) =>
        !string.IsNullOrWhiteSpace(messageId)
        && messages.Any(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal));

    public static bool ShouldHideMessageFromChat(ChatMessage message) =>
        message.Role == MessageRole.User && SummaryMessageBuilder.IsSummaryMessage(message)
        || ChatMessageViewModel.IsAssistantToolCallsOnly(message);
}
