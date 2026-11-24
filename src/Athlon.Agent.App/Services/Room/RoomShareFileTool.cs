using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Share a workspace file into the active LAN chat room as an attachment bubble.</summary>
public sealed class RoomShareFileTool(RoomStore roomStore, WorkspaceGuard workspaceGuard) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "room_share_file",
        "Share a file from the host workspace into the LAN chat room so all members can download it. "
            + "Use after creating or editing a document (e.g. Word/docx) that should appear in the room.",
        ToolSchema.Object()
            .String("path", "Absolute or workspace-relative path of the file to share.", required: true, minLength: 1)
            .String("caption", "Optional short note posted with the file.")
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var roomId = RoomAgentTurnScope.Current?.Options.RoomId;
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return Task.FromResult(ToolResult.Failure(
                "Unavailable",
                "room_share_file is only available during a room @agent turn."));
        }

        if (!invocation.Arguments.TryGetString("path", out var rawPath) || string.IsNullOrWhiteSpace(rawPath))
        {
            return Task.FromResult(ToolResult.Failure("Invalid arguments", "path is required."));
        }

        rawPath = rawPath.Trim();
        string fullPath;
        try
        {
            fullPath = workspaceGuard.Normalize(rawPath);
            if (workspaceGuard.HasConfiguredWorkspace && !workspaceGuard.IsInsideWorkspace(fullPath))
            {
                return Task.FromResult(ToolResult.Failure(
                    "Outside workspace",
                    "Path must be inside the active workspace."));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failure("Path error", ex.Message));
        }

        if (!File.Exists(fullPath))
        {
            return Task.FromResult(ToolResult.Failure("Not found", $"File not found: {rawPath}"));
        }

        try
        {
            var workspaceRoot = roomStore.GetRoom(roomId)?.WorkspaceRoot;
            // Reference the workspace file in place — no second copy under rooms/ or .athlon-room.
            var attachment = roomStore.SaveAttachmentFromPath(roomId, fullPath, workspaceRoot);
            var caption = invocation.Arguments.TryGetString("caption", out var cap) ? cap : null;

            var kind = attachment.IsImage ? "image" : "file";
            roomStore.AppendMessage(roomId, new RoomMessage
            {
                SenderId = RoomAgentIds.SenderId,
                DisplayName = RoomAgentIds.DisplayName,
                Kind = kind,
                Text = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
                Attachments = [attachment],
                CreatedAt = DateTimeOffset.UtcNow
            });

            return Task.FromResult(ToolResult.Success(
                $"Shared {attachment.FileName} ({attachment.Size} bytes) to the room."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failure("Share failed", ex.Message));
        }
    }
}
