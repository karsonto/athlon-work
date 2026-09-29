namespace Athlon.Agent.Core.Compaction;

public static class SessionHandoffNote
{
    public const string Marker = "[session-handoff]";

    public const int MaxChars = 16_000;

    public static ChatMessage CreateMessage(string note) =>
        ChatMessage.Create(MessageRole.User, Marker + "\n" + note.Trim());

    public static bool IsHandoffMessage(ChatMessage message) =>
        message.Role == MessageRole.User
        && message.Content.StartsWith(Marker, StringComparison.Ordinal);
}
