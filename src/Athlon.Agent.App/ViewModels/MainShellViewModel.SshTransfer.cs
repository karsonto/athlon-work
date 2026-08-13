using System.IO;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public partial class MainShellViewModel
{
    [RelayCommand(CanExecute = nameof(CanDownloadWorkspaceTreeNode))]
    private async Task DownloadWorkspaceTreeNodeAsync(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanDownloadWorkspaceTreeNode(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        try
        {
            if (node.IsDirectory)
            {
                var folderDialog = new OpenFolderDialog
                {
                    Title = _loc["Shell_DownloadFolderDialogTitle"]
                };
                if (folderDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(folderDialog.FolderName))
                {
                    return;
                }

                var localRoot = Path.Combine(folderDialog.FolderName, node.Name);
                var (downloaded, skipped) = await _sshTransfer.DownloadDirectoryRecursiveAsync(
                    node.FullPath,
                    localRoot,
                    _workspaceContext.IgnorePatterns).ConfigureAwait(true);
                Settings.SettingsStatus = _loc.Format("Shell_DownloadCompleteStatus", downloaded, skipped);
            }
            else
            {
                var saveDialog = new SaveFileDialog
                {
                    Title = _loc["Shell_DownloadFileDialogTitle"],
                    FileName = node.Name,
                    Filter = _loc["Shell_DownloadFileFilter"]
                };
                if (saveDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(saveDialog.FileName))
                {
                    return;
                }

                if (File.Exists(saveDialog.FileName)
                    && !_notifier.ConfirmYesNo("Shell_OverwriteTitle", "Shell_OverwriteLocalMessage", Path.GetFileName(saveDialog.FileName)))
                {
                    Settings.SettingsStatus = _loc["Shell_DownloadSkippedStatus"];
                    return;
                }

                await _sshTransfer.DownloadFileAsync(node.FullPath, saveDialog.FileName).ConfigureAwait(true);
                Settings.SettingsStatus = _loc.Format("Shell_DownloadFileSuccess", node.Name);
            }
        }
        catch (Exception exception)
        {
            _notifier.Warning("Shell_DownloadFailedTitle", "Shell_DownloadFailedMessage", node.Name, exception.Message);
            Settings.SettingsStatus = _loc.Format("Shell_DownloadFailedStatus", exception.Message);
        }
    }

    private bool CanDownloadWorkspaceTreeNode(WorkspaceTreeNodeViewModel? node) =>
        node is not null
        && node.IsRemote
        && !node.IsPlaceholder
        && !node.IsExpanderPlaceholder
        && !string.IsNullOrWhiteSpace(node.FullPath)
        && _workspaceContext.Kind == WorkspaceKind.Ssh
        && _sshClient.IsConnected;

    public bool CanAcceptRemoteFileDrop =>
        _workspaceContext.Kind == WorkspaceKind.Ssh
        && _sshClient.IsConnected
        && !string.IsNullOrWhiteSpace(_workspaceContext.RootPath);

    public async Task UploadLocalPathsToRemoteAsync(
        IReadOnlyList<string> localPaths,
        string? hitNodeFullPath,
        bool hitNodeIsDirectory)
    {
        if (!CanAcceptRemoteFileDrop || localPaths.Count == 0 || string.IsNullOrWhiteSpace(_workspaceContext.RootPath))
        {
            return;
        }

        var remoteDirectory = RemoteWorkspaceTransferHelper.ResolveDropTargetDirectory(
            _workspaceContext.RootPath,
            hitNodeFullPath,
            hitNodeIsDirectory);
        if (!RemoteWorkspaceTransferHelper.IsRemoteTargetAllowed(_workspaceContext.RootPath, remoteDirectory))
        {
            _notifier.Warning("Shell_UploadFailedTitle", "Shell_UploadTargetInvalid");
            return;
        }

        var uploaded = 0;
        var skipped = 0;
        try
        {
            foreach (var localPath in localPaths)
            {
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    continue;
                }

                if (File.Exists(localPath))
                {
                    var result = await _sshTransfer.UploadFileAsync(localPath, remoteDirectory).ConfigureAwait(true);
                    if (result) uploaded++; else skipped++;
                }
                else if (Directory.Exists(localPath))
                {
                    var (up, skip) = await _sshTransfer.UploadDirectoryRecursiveAsync(localPath, remoteDirectory).ConfigureAwait(true);
                    uploaded += up;
                    skipped += skip;
                }
            }

            await RefreshWorkspaceTreeAsync().ConfigureAwait(true);
            Settings.SettingsStatus = _loc.Format("Shell_UploadCompleteStatus", uploaded, skipped);
        }
        catch (Exception exception)
        {
            _notifier.Warning("Shell_UploadFailedTitle", "Shell_UploadFailedMessage", exception.Message);
            Settings.SettingsStatus = _loc.Format("Shell_UploadFailedStatus", exception.Message);
            try
            {
                await RefreshWorkspaceTreeAsync().ConfigureAwait(true);
            }
            catch
            {
                // ignore refresh errors after failure
            }
        }
    }
}
