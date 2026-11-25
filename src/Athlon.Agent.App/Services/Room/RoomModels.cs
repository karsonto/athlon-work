using System.Text.Json.Serialization;

namespace Athlon.Agent.App.Services.Room;

public static class RoomAgentIds
{
    public const string SenderId = "athlon-agent";
    public const string DisplayName = "Athlon";
    public const string ProfileId = "room-agent";
}

public static class RoomAgentRuntimeModes
{
    public const string Local = "local";
    public const string Sandbox = "sandbox";
    public const string CloudSandbox = "cloud_sandbox";

    public static bool IsSandbox(string? mode)
    {
        var n = Normalize(mode);
        return n is Sandbox or CloudSandbox;
    }

    public static bool IsLocalDockerSandbox(string? mode) =>
        string.Equals(Normalize(mode), Sandbox, StringComparison.OrdinalIgnoreCase);

    public static bool IsCloudSandbox(string? mode) =>
        string.Equals(Normalize(mode), CloudSandbox, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? mode)
    {
        var trimmed = mode?.Trim();
        if (string.Equals(trimmed, Sandbox, StringComparison.OrdinalIgnoreCase))
        {
            return Sandbox;
        }

        if (string.Equals(trimmed, CloudSandbox, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return CloudSandbox;
        }

        return Local;
    }

    public static string DisplayLabel(string? mode) =>
        Normalize(mode) switch
        {
            Sandbox => "Sandbox",
            CloudSandbox => "云沙箱",
            _ => "本地"
        };
}

public sealed class RoomInfo
{
    public string Id { get; set; } = "";

    public string Token { get; set; } = "";

    public string Title { get; set; } = "Athlon 团队房间";

    /// <summary>Host workspace root where @agent runs tools for this room.</summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>Optional configured workspace id (local or SSH).</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>local | sandbox | cloud_sandbox — where @agent file/shell tools execute.</summary>
    public string AgentRuntimeMode { get; set; } = RoomAgentRuntimeModes.Local;

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

    /// <summary>Stable senderIds of teammates @-mentioned in this message (excludes @agent).</summary>
    public List<string> MentionedSenderIds { get; set; } = new();

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

    public string AgentRuntimeMode { get; init; } = RoomAgentRuntimeModes.Local;
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

    [JsonPropertyName("mentionedSenderIds")]
    public List<string>? MentionedSenderIds { get; set; }
}

public enum RoomStoreEventKind
{
    Message,
    MessageUpdated
}

public readonly record struct RoomStoreEvent(RoomStoreEventKind Kind, RoomMessage Message);

public sealed class RoomPresenceMember
{
    public string SenderId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RoomPresenceSnapshot
{
    public string RoomId { get; set; } = "";

    /// <summary>Current room title, so rename reaches members already connected.</summary>
    public string Title { get; set; } = "";

    public List<RoomPresenceMember> Members { get; set; } = new();

    public int Count => Members.Count;
}

/// <summary>Host-only live trace of @agent tool/model activity (not persisted to chat history).</summary>
public sealed class RoomAgentTraceEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string RoomId { get; set; } = "";

    /// <summary>Agent chat bubble message id this run is attached to.</summary>
    public string MessageId { get; set; } = "";

    /// <summary>run_started | tool_start | tool_args | tool_result | reasoning | run_finished | error</summary>
    public string Type { get; set; } = "";

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public string? Summary { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
