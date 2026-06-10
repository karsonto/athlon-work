using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Compaction;

internal static class ConversationMessageFilters
{
    public static List<ChatMessage> WithoutCompactionAudits(IEnumerable<ChatMessage> messages) =>
        messages.Where(message => message.Role != MessageRole.Compaction).ToList();
}
