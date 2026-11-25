using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Share a workspace file into the active LAN chat room as an attachment bubble.</summary>
public sealed class RoomShareFileTool(
    RoomStore roomStore,
    WorkspaceGuard workspaceGuard,
    AppSettings settings,
    RoomSshAttachmentService sshAttachments) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "room_share_file",
        "Share a file from the host workspace into the LAN chat room so all members can download it. "
            + "Use after creating or editing a document (e.g. Word/docx) that should appear in the room.",
        ToolSchema.Object()
            .String("path", "Absolute or workspace-relative path of the file to share.", required: true, minLength: 1)
            .String("caption", "Optional short note posted with the file.")
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var roomId = RoomAgentTurnScope.Current?.Options.RoomId;
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return ToolResult.Failure(
                "Unavailable",
                "room_share_file is only available during a room @agent turn.");
        }

        if (!invocation.Arguments.TryGetString("path", out var rawPath) || string.IsNullOrWhiteSpace(rawPath))
        {
            return ToolResult.Failure("Invalid arguments", "path is required.");
        }

        rawPath = rawPath.Trim();
        string fullPath;
        try
        {
            fullPath = workspaceGuard.Normalize(rawPath);
            if (workspaceGuard.HasConfiguredWorkspace && !workspaceGuard.IsInsideWorkspace(fullPath))
            {
                return ToolResult.Failure(
                    "Outside workspace",
                    "Path must be inside the active workspace.");
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Path error", ex.Message);
        }

        var caption = invocation.Arguments.TryGetString("caption", out var cap) ? cap : null;
        var binding = RoomWorkspaceBinding.Resolve(settings, roomStore.GetRoom(roomId));

        try
        {
            if (binding.IsSsh)
            {
                var attachment = await sshAttachments
                    .ShareRemoteFileAsync(roomId, fullPath, caption, cancellationToken)
                    .ConfigureAwait(false);
                return ToolResult.Success(
                    $"Shared {attachment.FileName} ({attachment.Size} bytes) to the room.");
            }

            if (!File.Exists(fullPath))
            {
                return ToolResult.Failure("Not found", $"File not found: {rawPath}");
            }

            var workspaceRoot = roomStore.GetRoom(roomId)?.WorkspaceRoot;
            // Reference the workspace file in place — no second copy under rooms/ or .athlon-room.
            var localAttachment = roomStore.SaveAttachmentFromPath(roomId, fullPath, workspaceRoot);

            var kind = localAttachment.IsImage ? "image" : "file";
            roomStore.AppendMessage(roomId, new RoomMessage
            {
                SenderId = RoomAgentIds.SenderId,
                DisplayName = RoomAgentIds.DisplayName,
                Kind = kind,
                Text = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
                Attachments = [localAttachment],
                CreatedAt = DateTimeOffset.UtcNow
            });

            return ToolResult.Success(
                $"Shared {localAttachment.FileName} ({localAttachment.Size} bytes) to the room.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Share failed", ex.Message);
        }
    }
}
