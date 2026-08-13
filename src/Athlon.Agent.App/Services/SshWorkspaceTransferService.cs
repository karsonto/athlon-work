using System.IO;
using Athlon.Agent.App.Localization;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;

namespace Athlon.Agent.App.Services;

/// <summary>Recursive SSH download/upload used by the workspace tree. UI dialogs stay in the shell VM.</summary>
public sealed class SshWorkspaceTransferService(ISshWorkspaceClient sshClient, IUserNotifier notifier)
{
    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        sshClient.DownloadFileAsync(remotePath, localPath, cancellationToken);

    public async Task<(int Downloaded, int Skipped)> DownloadDirectoryRecursiveAsync(
        string remoteDirectory,
        string localDirectory,
        IReadOnlyList<string> ignorePatterns,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(localDirectory);
        var downloaded = 0;
        var skipped = 0;

        await foreach (var entry in sshClient.ListAsync(remoteDirectory, cancellationToken).ConfigureAwait(true))
        {
            if (SshWorkspaceToolHelper.ShouldIgnore(entry.FullPath, ignorePatterns))
            {
                continue;
            }

            var localChild = Path.Combine(localDirectory, entry.Name);
            if (entry.IsDirectory)
            {
                var (childDownloaded, childSkipped) = await DownloadDirectoryRecursiveAsync(
                    entry.FullPath,
                    localChild,
                    ignorePatterns,
                    cancellationToken).ConfigureAwait(true);
                downloaded += childDownloaded;
                skipped += childSkipped;
                continue;
            }

            if (File.Exists(localChild)
                && !notifier.ConfirmYesNo("Shell_OverwriteTitle", "Shell_OverwriteLocalMessage", entry.Name))
            {
                skipped++;
                continue;
            }

            await sshClient.DownloadFileAsync(entry.FullPath, localChild, cancellationToken).ConfigureAwait(true);
            downloaded++;
        }

        return (downloaded, skipped);
    }

    public async Task<bool> UploadFileAsync(string localPath, string remoteDirectory, CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(localPath);
        var remotePath = RemotePathNormalizer.Combine(remoteDirectory, fileName);
        var existing = await sshClient.TryGetFileInfoAsync(remotePath, cancellationToken).ConfigureAwait(true);
        if (existing is not null
            && !notifier.ConfirmYesNo("Shell_OverwriteTitle", "Shell_OverwriteRemoteMessage", fileName))
        {
            return false;
        }

        await sshClient.UploadFileAsync(localPath, remotePath, cancellationToken).ConfigureAwait(true);
        return true;
    }

    public async Task<(int Uploaded, int Skipped)> UploadDirectoryRecursiveAsync(
        string localDirectory,
        string remoteParentDirectory,
        CancellationToken cancellationToken = default)
    {
        var folderName = Path.GetFileName(localDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var remoteDirectory = RemotePathNormalizer.Combine(remoteParentDirectory, folderName);
        await sshClient.CreateDirectoryAsync(remoteDirectory, cancellationToken).ConfigureAwait(true);

        var uploaded = 0;
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(localDirectory))
        {
            if (await UploadFileAsync(file, remoteDirectory, cancellationToken).ConfigureAwait(true))
            {
                uploaded++;
            }
            else
            {
                skipped++;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(localDirectory))
        {
            var (up, skip) = await UploadDirectoryRecursiveAsync(child, remoteDirectory, cancellationToken).ConfigureAwait(true);
            uploaded += up;
            skipped += skip;
        }

        return (uploaded, skipped);
    }
}
