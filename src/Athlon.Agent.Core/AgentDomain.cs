using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
    Summary,
    Compaction
}

public sealed record ImageAttachment(
    string FileName,
    string MimeType,
    string? DataUrl = null,
    string? LocalPath = null);

public sealed record ChatMessage(
    string Id,
    MessageRole Role,
    string Content,
    DateTimeOffset CreatedAt,
    string? ParentId = null,
    string? ToolCallsJson = null,
    string? ReasoningContent = null,
    IReadOnlyList<ImageAttachment>? ImageAttachments = null)
{
    public static ChatMessage Create(
        MessageRole role,
        string content,
        string? parentId = null,
        IReadOnlyList<AgentToolCall>? toolCalls = null,
        string? reasoningContent = null,
        IReadOnlyList<ImageAttachment>? imageAttachments = null) =>
        CreateWithId(
            Guid.NewGuid().ToString("N"),
            role,
            content,
            parentId,
            toolCalls,
            reasoningContent,
            imageAttachments);

    public static ChatMessage CreateWithId(
        string id,
        MessageRole role,
        string content,
        string? parentId = null,
        IReadOnlyList<AgentToolCall>? toolCalls = null,
        string? reasoningContent = null,
        IReadOnlyList<ImageAttachment>? imageAttachments = null) =>
        new(
            id,
            role,
            content,
            DateTimeOffset.UtcNow,
            parentId,
            AssistantToolCallsCodec.Serialize(toolCalls ?? Array.Empty<AgentToolCall>()),
            reasoningContent,
            imageAttachments);

    /// <summary>
    /// Stable id for a tool result row so a running placeholder can be upserted into the final result.
    /// </summary>
    public static string ToolResultMessageId(string toolCallId) =>
        "tool:" + toolCallId;
}
public sealed record AgentSession(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ActiveWorkspace,
    string? ActiveSkill,
    string? ModelName,
    IReadOnlyList<ChatMessage> Messages)
{
    /// <summary>When set, points at <see cref="WorkspaceSettings.Id"/> (used for SSH workspaces).</summary>
    public string? ActiveWorkspaceId { get; init; }

    public static AgentSession Create(string title = "New chat") =>
        new(
            Guid.NewGuid().ToString("N"),
            title,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            Array.Empty<ChatMessage>());

    public AgentSession WithMessage(ChatMessage message) => this with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Messages = Messages.Concat(new[] { message }).ToArray()
    };

    /// <summary>Replaces an existing message with the same Id, or appends when absent.</summary>
    public AgentSession WithUpsertedMessage(ChatMessage message)
    {
        var messages = Messages.ToList();
        for (var index = 0; index < messages.Count; index++)
        {
            if (string.Equals(messages[index].Id, message.Id, StringComparison.Ordinal))
            {
                messages[index] = message;
                return this with
                {
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Messages = messages.ToArray()
                };
            }
        }

        messages.Add(message);
        return this with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = messages.ToArray()
        };
    }

    public AgentSession WithWorkspace(string? workspaceRootPath, string? workspaceId = null) => this with
    {
        ActiveWorkspace = workspaceRootPath,
        ActiveWorkspaceId = workspaceId,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public AgentSession WithTitle(string title) => this with
    {
        Title = title,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public AgentSession WithMessages(IReadOnlyList<ChatMessage> messages) => this with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Messages = messages.ToArray()
    };
}
public sealed record ContextSummary(string Id, string SessionId, string Content, int OriginalMessageCount, DateTimeOffset CreatedAt);
public sealed record SessionIndexEntry(
    string Id,
    string Title,
    string Path,
    DateTimeOffset UpdatedAt,
    int? MessageCount = null,
    string? ActiveWorkspace = null,
    string? ActiveWorkspaceId = null);

public sealed record ConversationDisplayCursor(
    long ByteOffset,
    IReadOnlyList<string> SeenMessageIds);

public sealed record ConversationDisplayPage(
    IReadOnlyList<ChatMessage> Messages,
    ConversationDisplayCursor? OlderCursor);

/// <summary>Display-path page sizes for conversation history (UI + storage defaults).</summary>
public static class ConversationDisplayLimits
{
    public const int PageSize = 40;
    public const int WebViewReplayBatchSize = 15;
    public const int UiHydrateBatchSize = 20;
}
