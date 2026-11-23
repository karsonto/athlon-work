using System.Text.Json.Serialization;

namespace Athlon.Agent.App.Services.Room;

public sealed class RoomInfo
{
    public string Id { get; set; } = "";

    public string Token { get; set; } = "";

    public string Title { get; set; } = "Athlon 聊天房间";

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
}

public sealed class RoomMessage
{
    public string Id { get; set; } = "";

    public string SenderId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>text | image | file</summary>
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
