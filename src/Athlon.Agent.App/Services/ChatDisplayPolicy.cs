using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

internal static class ChatDisplayPolicy
{
    public static bool ShouldShowToolCalls(bool showToolCalls) => showToolCalls;

    public static bool ShouldIncludeToolMessage(bool showToolCalls, ChatMessage message) =>
        message.Role != MessageRole.Tool || showToolCalls;

    public static bool ShouldIncludeToolViewModel(bool showToolCalls, ChatMessageViewModel vm) =>
        !vm.IsTool || vm.IsCompaction || showToolCalls;

    public static bool IsToolStreamEvent(AgentStreamEvent streamEvent) =>
        streamEvent is AgentStreamEvent.ToolCallStart
            or AgentStreamEvent.ToolCallArgs
            or AgentStreamEvent.ToolCallEnd
            or AgentStreamEvent.ToolCallResult
            or AgentStreamEvent.ToolCallOutput;
}
