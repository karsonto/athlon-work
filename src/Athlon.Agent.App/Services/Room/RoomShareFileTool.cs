using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Share a workspace file into the active LAN chat room as an attachment bubble.</summary>
public sealed class RoomShareFileTool(
    RoomStore roomStore,
    WorkspaceGuard workspaceGuard,
    AppSettings settings,
    RoomSshAttachmentService sshAttachments,
    IRoomSandboxBackendResolver sandboxResolver,
    IAppPathProvider paths) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "room_share_file",
        "Share a file from the host workspace into the LAN chat room so all members can download it. "
            + "Use after creating or editing a document (e.g. Word/docx) that should appear in the room. "
            + "In cloud sandbox mode the file is fetched from the remote session if it is not on the host yet.",
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

            var room = roomStore.GetRoom(roomId);
            var workspaceRoot = room?.WorkspaceRoot;
            await MaterializeFromCloudSandboxIfNeededAsync(
                    roomId,
                    room,
                    fullPath,
                    workspaceRoot,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!File.Exists(fullPath))
            {
                return ToolResult.Failure("Not found", $"File not found: {rawPath}");
            }

            // Reference the workspace file in place — no second copy under rooms/ or user-upload.
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

    /// <summary>
    /// Cloud Docker sessions are not bind-mounted; host only updates on PullWorkspace (turn end).
    /// Fetch the single file via gRPC before sharing so mid-turn room_share_file works.
    /// </summary>
    private async Task MaterializeFromCloudSandboxIfNeededAsync(
        string roomId,
        RoomInfo? room,
        string fullPath,
        string? workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (room is null
            || !RoomAgentRuntimeModes.IsCloudSandbox(room.AgentRuntimeMode)
            || string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        var sandbox = sandboxResolver.Resolve(room.AgentRuntimeMode);
        if (sandbox is null)
        {
            return;
        }

        RoomSandboxPathMap map;
        try
        {
            map = new RoomSandboxPathMap(workspaceRoot, paths.SkillsPath);
        }
        catch
        {
            return;
        }

        var containerPath = map.ToContainer(fullPath);
        if (string.IsNullOrWhiteSpace(containerPath))
        {
            return;
        }

        // Prefer remote bytes when present so share sees the just-produced cloud file.
        byte[]? bytes;
        try
        {
            bytes = await sandbox.ReadFileAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            bytes = null;
        }

        if (bytes is null || bytes.Length == 0)
        {
            // Empty file is rare but valid; distinguish missing vs empty via PathExists.
            try
            {
                if (!await sandbox.PathExistsAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                bytes ??= Array.Empty<byte>();
            }
            catch
            {
                return;
            }
        }

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);
    }
}
