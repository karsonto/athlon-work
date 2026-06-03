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
    string DataUrl);

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
        new(
            Guid.NewGuid().ToString("N"),
            role,
            content,
            DateTimeOffset.UtcNow,
            parentId,
            AssistantToolCallsCodec.Serialize(toolCalls ?? Array.Empty<AgentToolCall>()),
            reasoningContent,
            imageAttachments);
}
public sealed record AgentSession(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ActiveWorkspace,
    string? ActiveSkill,
    string? ModelName,
    IReadOnlyList<ChatMessage> Messages,
    AgentInteractionMode InteractionMode = AgentInteractionMode.Agent)
{
    public static AgentSession Create(string title = "New chat") =>
        new(
            Guid.NewGuid().ToString("N"),
            title,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            Array.Empty<ChatMessage>(),
            AgentInteractionMode.Agent);

    public AgentSession WithMessage(ChatMessage message) => this with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Messages = Messages.Concat(new[] { message }).ToArray()
    };

    public AgentSession WithWorkspace(string? workspaceRootPath) => this with
    {
        ActiveWorkspace = workspaceRootPath,
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

    public AgentSession WithInteractionMode(AgentInteractionMode mode) => this with
    {
        InteractionMode = mode,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
public sealed record ContextSummary(string Id, string SessionId, string Content, int OriginalMessageCount, DateTimeOffset CreatedAt);
public sealed record SessionIndexEntry(string Id, string Title, string Path, DateTimeOffset UpdatedAt);
