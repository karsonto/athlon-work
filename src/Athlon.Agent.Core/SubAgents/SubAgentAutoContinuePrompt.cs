using Athlon.Agent.Core;

namespace Athlon.Agent.Core.SubAgents;

public static class SubAgentAutoContinuePrompt
{
    public const string Marker = "<athlon-subagent-auto-continue />";

    public static string BuildUserMessage() =>
        "A background sub-agent has completed its task.\n\n" +
        Marker +
        "\n\n" +
        "Review the sub-agent completion injected in your context and write a concise summary for the user " +
        "(findings, conclusions, and suggested next steps). Do not expose internal session keys unless asked.";

    public static bool IsAutoContinueMessage(ChatMessage message) =>
        message.Role == MessageRole.User
        && message.Content.Contains(Marker, StringComparison.Ordinal);
}
