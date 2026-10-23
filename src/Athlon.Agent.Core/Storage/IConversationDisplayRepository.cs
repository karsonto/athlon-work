namespace Athlon.Agent.Core.Storage;

public interface IConversationDisplayRepository
{
    Task<ChatMessage?> TryLoadConversationMessageAsync(
        string sessionId,
        string messageId,
        CancellationToken cancellationToken = default);

    Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default);

    Task ReplaceConversationDisplayAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task ReplaceConversationDisplayOffThreadAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<ConversationDisplayPage> LoadConversationDisplayPageAsync(
        string sessionId,
        ConversationDisplayCursor? cursor = null,
        int pageSize = ConversationDisplayLimits.PageSize,
        CancellationToken cancellationToken = default);

    Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default);
}
