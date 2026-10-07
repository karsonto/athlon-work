namespace Athlon.Agent.Core.Compaction;

public static class ConversationMessageFilters
{
    public static List<ChatMessage> WithoutCompactionAudits(IEnumerable<ChatMessage> messages) =>
        messages.Where(message => message.Role != MessageRole.Compaction).ToList();

    public static List<ChatMessage> WithoutHandoffNotes(IEnumerable<ChatMessage> messages) =>
        messages.Where(message => !SessionHandoffNote.IsHandoffMessage(message)).ToList();
}
