using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

public sealed record SessionForkMaterialization(
    AgentSession Session,
    IReadOnlyList<ImageAttachment> ComposerImages);

/// <summary>
/// Copies attachment files and evicted tool results into the forked session directory.
/// Transcripts are left behind so later messages from the original chat are not visible.
/// </summary>
public static class SessionForkFiles
{
    public static SessionForkMaterialization Materialize(
        AgentSession source,
        SessionForkSlice slice,
        IAppPathProvider paths,
        IImageAttachmentStore images)
    {
        var session = SessionFork.CreateSession(source, Array.Empty<ChatMessage>());
        var prefix = slice.Prefix
            .Select(message => RewriteImages(message, source.Id, session.Id, paths, images))
            .ToArray();
        session = session.WithMessages(prefix);
        CopyEvictedToolResults(source.Id, session.Id, SessionFork.ToolCallIds(prefix), paths);
        var composerImages = RewriteImageList(slice.ComposerMessage.ImageAttachments, source.Id, session.Id, paths, images);
        return new SessionForkMaterialization(session, composerImages ?? Array.Empty<ImageAttachment>());
    }

    private static void CopyEvictedToolResults(
        string sourceSessionId,
        string destinationSessionId,
        IReadOnlyList<string> toolCallIds,
        IAppPathProvider paths)
    {
        if (toolCallIds.Count == 0)
        {
            return;
        }

        var sourceDir = Path.GetFullPath(Path.Combine(paths.SessionsPath, sourceSessionId, "evicted"));
        var destinationDir = Path.GetFullPath(Path.Combine(paths.SessionsPath, destinationSessionId, "evicted"));
        foreach (var toolCallId in toolCallIds)
        {
            if (!IsSafeFileName(toolCallId))
            {
                continue;
            }

            var source = Path.GetFullPath(Path.Combine(sourceDir, toolCallId + ".txt"));
            if (!source.StartsWith(sourceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(source))
            {
                continue;
            }

            Directory.CreateDirectory(destinationDir);
            var destination = Path.GetFullPath(Path.Combine(destinationDir, toolCallId + ".txt"));
            if (!destination.StartsWith(destinationDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(source, destination, overwrite: true);
        }
    }

    private static ChatMessage RewriteImages(
        ChatMessage message,
        string sourceSessionId,
        string destinationSessionId,
        IAppPathProvider paths,
        IImageAttachmentStore images)
    {
        var rewritten = RewriteImageList(message.ImageAttachments, sourceSessionId, destinationSessionId, paths, images);
        if (ReferenceEquals(rewritten, message.ImageAttachments) || rewritten is null)
        {
            return message;
        }

        return message with { ImageAttachments = rewritten };
    }

    private static IReadOnlyList<ImageAttachment>? RewriteImageList(
        IReadOnlyList<ImageAttachment>? attachments,
        string sourceSessionId,
        string destinationSessionId,
        IAppPathProvider paths,
        IImageAttachmentStore images)
    {
        if (attachments is not { Count: > 0 })
        {
            return attachments;
        }

        var changed = false;
        var rewritten = new ImageAttachment[attachments.Count];
        for (var index = 0; index < attachments.Count; index++)
        {
            rewritten[index] = CopySessionImage(attachments[index], sourceSessionId, destinationSessionId, paths, images);
            changed |= !ReferenceEquals(rewritten[index], attachments[index]);
        }

        return changed ? rewritten : attachments;
    }

    private static ImageAttachment CopySessionImage(
        ImageAttachment attachment,
        string sourceSessionId,
        string destinationSessionId,
        IAppPathProvider paths,
        IImageAttachmentStore images)
    {
        if (string.IsNullOrWhiteSpace(attachment.LocalPath) || !File.Exists(attachment.LocalPath))
        {
            return attachment;
        }

        var attachmentsRoot = Path.GetFullPath(Path.Combine(paths.SessionsPath, sourceSessionId, "attachments"));
        var fullPath = Path.GetFullPath(attachment.LocalPath);
        if (!fullPath.StartsWith(attachmentsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return attachment;
        }

        try
        {
            var saved = images.SaveFromFile(destinationSessionId, fullPath);
            return saved with { FileName = attachment.FileName };
        }
        catch (Exception)
        {
            return attachment;
        }
    }

    private static bool IsSafeFileName(string name) =>
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name is not "." and not "..";
}
