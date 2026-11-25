using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;
using Microsoft.Extensions.Logging;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Mirrors room chat attachments into an SSH workspace <c>.athlon-room/{roomId}</c> tree.</summary>
public sealed class RoomSshAttachmentService(
    RoomStore store,
    AppSettings settings,
    SshWorkspaceConnectionService sshConnection,
    ISshWorkspaceClient sshClient,
    ILogger<RoomSshAttachmentService> logger)
{
    public async Task<RoomAttachmentInfo> SaveUploadAsync(
        string roomId,
        string fileName,
        string mimeType,
        Stream content,
        long? declaredLength,
        CancellationToken cancellationToken = default)
    {
        var binding = RoomWorkspaceBinding.Resolve(settings, store.GetRoom(roomId));
        if (!binding.IsSsh || !binding.IsConfigured || binding.Settings is null)
        {
            return store.SaveAttachment(
                roomId,
                fileName,
                mimeType,
                content,
                declaredLength,
                binding.IsConfigured && !binding.IsSsh ? binding.Root : null);
        }

        // Always keep a local downloadable blob; then upload a remote mirror for @agent.
        var info = store.SaveAttachment(roomId, fileName, mimeType, content, declaredLength, workspaceRoot: null);
        var localPath = store.GetAttachmentFilePath(roomId, info.Id)
            ?? throw new InvalidOperationException("Local attachment blob missing after save.");

        var sessionId = RoomAgentSessionStore.StableSessionId(roomId);
        var session = AgentSession.Create($"Room {roomId}") with { Id = sessionId };
        session = session.WithWorkspace(binding.Root!, binding.Id);

        await sshConnection.SyncAsync(session, settings, cancellationToken).ConfigureAwait(false);

        var remoteDir = RoomWorkspaceBinding.AttachmentMirrorDir(binding.Root!, roomId, WorkspaceKind.Ssh);
        var remotePath = RemotePathNormalizer.Combine(remoteDir, info.FileName);
        using (SshRootSessionScope.EnterIfAbsent(sessionId))
        {
            if (await sshClient.FileExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
            {
                var stem = Path.GetFileNameWithoutExtension(info.FileName);
                var ext = Path.GetExtension(info.FileName);
                var suffix = info.Id[..Math.Min(8, info.Id.Length)];
                remotePath = RemotePathNormalizer.Combine(remoteDir, $"{stem}_{suffix}{ext}");
            }

            await sshClient.CreateDirectoryAsync(remoteDir, cancellationToken).ConfigureAwait(false);
            await sshClient.UploadFileAsync(localPath, remotePath, cancellationToken).ConfigureAwait(false);
        }

        info.WorkspacePath = remotePath;
        info.StoredName = Path.GetFileName(remotePath.TrimEnd('/')) is { Length: > 0 } name
            ? name
            : info.StoredName;
        store.WriteAttachmentMeta(roomId, info);
        logger.LogDebug("Mirrored room attachment {AttachmentId} to SSH {RemotePath}", info.Id, remotePath);
        return info;
    }

    public async Task<RoomAttachmentInfo> ShareRemoteFileAsync(
        string roomId,
        string remoteFullPath,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        var binding = RoomWorkspaceBinding.Resolve(settings, store.GetRoom(roomId));
        if (!binding.IsSsh || binding.Settings is null || string.IsNullOrWhiteSpace(binding.Root))
        {
            throw new InvalidOperationException("Room is not bound to an SSH workspace.");
        }

        var remotePath = RemotePathNormalizer.Collapse(remoteFullPath);
        if (!RemotePathNormalizer.IsUnderRoot(remotePath, binding.Root))
        {
            throw new InvalidOperationException("Path must be inside the active workspace.");
        }

        var sessionId = RoomAgentSessionStore.StableSessionId(roomId);
        var session = AgentSession.Create($"Room {roomId}") with { Id = sessionId };
        session = session.WithWorkspace(binding.Root!, binding.Id);
        await sshConnection.SyncAsync(session, settings, cancellationToken).ConfigureAwait(false);

        var fileName = RemotePathNormalizer.GetFileName(remotePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file.bin";
        }

        var tempPath = Path.Combine(Path.GetTempPath(), "athlon-room-share-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (SshRootSessionScope.EnterIfAbsent(sessionId))
            {
                var remoteInfo = await sshClient.GetFileInfoAsync(remotePath, cancellationToken).ConfigureAwait(false);
                if (remoteInfo.IsDirectory)
                {
                    throw new InvalidOperationException("path must be a file, not a directory.");
                }

                await sshClient.DownloadFileAsync(remotePath, tempPath, cancellationToken).ConfigureAwait(false);
            }

            await using var fs = File.OpenRead(tempPath);
            var info = store.SaveAttachment(
                roomId,
                fileName,
                GuessMime(fileName),
                fs,
                new FileInfo(tempPath).Length,
                workspaceRoot: null);
            info.WorkspacePath = remotePath;
            store.WriteAttachmentMeta(roomId, info);

            var kind = info.IsImage ? "image" : "file";
            store.AppendMessage(roomId, new RoomMessage
            {
                SenderId = RoomAgentIds.SenderId,
                DisplayName = RoomAgentIds.DisplayName,
                Kind = kind,
                Text = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
                Attachments = [info],
                CreatedAt = DateTimeOffset.UtcNow
            });
            return info;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string GuessMime(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream"
        };
    }
}
