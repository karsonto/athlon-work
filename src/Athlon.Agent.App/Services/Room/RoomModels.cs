using System.Text.Json.Serialization;

namespace Athlon.Agent.App.Services.Room;

public static class RoomAgentIds
{
    public const string SenderId = "athlon-agent";
    public const string DisplayName = "Athlon";
    public const string ProfileId = "room-agent";
}

public sealed class RoomInfo
{
    public string Id { get; set; } = "";

    public string Token { get; set; } = "";

    public string Title { get; set; } = "Athlon 聊天房间";

    /// <summary>Host workspace root where @agent runs tools for this room.</summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>Optional configured workspace id (local or SSH).</summary>
    public string? WorkspaceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RoomAttachmentInfo
{
    public string Id { get; set; } = "";

    public string FileName { get; set; } = "";

    public string MimeType { get; set; } = "application/octet-stream";

    public long Size { get; set; }

    public bool IsImage { get; set; }

    public string StoredName { get; set; } = "";

    /// <summary>Absolute path under the room workspace when the upload was mirrored for @agent.</summary>
    public string? WorkspacePath { get; set; }
}

public sealed class RoomMessage
{
    public string Id { get; set; } = "";

    public string SenderId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>text | image | file | agent</summary>
    public string Kind { get; set; } = "text";

    public string? Text { get; set; }

    public List<RoomAttachmentInfo> Attachments { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RoomCreateResult
{
    public required RoomInfo Room { get; init; }

    public required string InviteUrl { get; init; }

    public required string LanIp { get; init; }

    public required int Port { get; init; }
}

public sealed class RoomSummary
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string InviteUrl { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? WorkspaceRoot { get; init; }

    public string? WorkspaceId { get; init; }

    public string? WorkspaceName { get; init; }
}

public sealed class PostMessageRequest
{
    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("attachmentIds")]
    public List<string>? AttachmentIds { get; set; }
}

public enum RoomStoreEventKind
{
    Message,
    MessageUpdated
}

public readonly record struct RoomStoreEvent(RoomStoreEventKind Kind, RoomMessage Message);
