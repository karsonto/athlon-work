using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Navigation;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Infrastructure.Ssh;
using MaterialDesignThemes.Wpf;

namespace Athlon.Agent.App.ViewModels;

/// <summary>
/// Workspace file/editor surface: opening workspace files in the editor or preview, workspace
/// tree context actions and the editor tab commands. Split out of the shell orchestration file.
/// </summary>
public partial class MainShellViewModel
{
    private async Task RemoveSessionWorkspaceAsync()
    {
        if (!HasSessionWorkspace)
        {
            return;
        }

        if (!await FileEditor.TryCloseAllTabsAsync().ConfigureAwait(true))
        {
            return;
        }

        await _sshConnection.DisconnectSessionAsync(_session.Id).ConfigureAwait(true);
        _session = _session.WithWorkspace(null, workspaceId: null);
        await ApplySessionWorkspaceAsync().ConfigureAwait(true);
        await SaveCurrentSessionIfNeededAsync().ConfigureAwait(true);
        ShowShellToast(_loc["Shell_WorkspaceRemoved"], ShellToastKind.Success);
    }

    private async Task OnSettingsSavedAsync()
    {
        _uiCache.ApplyShowToolCalls();
        await _activeUi.RefreshDisplayForSettingsAsync().ConfigureAwait(true);
        await RefreshMcpRuntimeAsync().ConfigureAwait(true);
        ApplySessionWorkspace();
        ComposerKnowledge.NotifyEmbeddingConfigurationChanged();
        // Read-aloud may have been just enabled/disabled; stop any utterance and republish the flag.
        await PushTtsConfigAsync().ConfigureAwait(true);
        CurrentPage = AppPage.Chat;
    }

    /// <summary>
    /// Stops in-flight read-aloud and re-publishes the enabled flag so the bubble button appears or
    /// disappears without a page reload.
    /// </summary>
    private async Task PushTtsConfigAsync()
    {
        var chatView = _savedChatView;
        if (chatView is null)
        {
            return;
        }

        chatView.TtsController?.Stop();
        await chatView.PushTtsConfigAsync().ConfigureAwait(true);
    }

    public Task OpenWorkspaceFileInEditorAsync(string path) =>
        FileEditor.OpenFileAsync(path, _session.ActiveWorkspace);

    public Task OpenWorkspaceFileForPreviewAsync(string relativeOrFullPath)
    {
        var fullPath = ResolveWorkspaceFilePath(relativeOrFullPath);
        if (fullPath is null)
        {
            _notifier.Info("Shell_CannotPreviewTitle", "Shell_CannotPreviewMessage");
            return Task.CompletedTask;
        }

        return FileEditor.OpenFileAsync(fullPath, _session.ActiveWorkspace, readOnly: true);
    }

    private string? ResolveWorkspaceFilePath(string relativeOrFullPath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrFullPath))
        {
            return null;
        }

        if (_workspaceContext.Kind == WorkspaceKind.Ssh)
        {
            if (string.IsNullOrWhiteSpace(_session.ActiveWorkspace))
            {
                return null;
            }

            var remote = PathUtil.ToForwardSlashes(relativeOrFullPath);
            return remote.StartsWith('/')
                ? RemotePathNormalizer.Collapse(remote)
                : RemotePathNormalizer.Combine(_session.ActiveWorkspace, remote);
        }

        if (Path.IsPathRooted(relativeOrFullPath))
        {
            return Path.GetFullPath(relativeOrFullPath);
        }

        if (string.IsNullOrWhiteSpace(_session.ActiveWorkspace))
        {
            return null;
        }

        var normalized = relativeOrFullPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(_session.ActiveWorkspace, normalized));
    }

    [RelayCommand(CanExecute = nameof(CanOpenWorkspaceTreeNodeInEditor))]
    private async Task OpenWorkspaceTreeNodeInEditorAsync(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanOpenWorkspaceTreeNodeInEditor(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        await OpenWorkspaceFileInEditorAsync(node.FullPath).ConfigureAwait(true);
    }

    private bool CanOpenWorkspaceTreeNodeInEditor(WorkspaceTreeNodeViewModel? node) =>
        node is not null
        && !node.IsPlaceholder
        && !node.IsExpanderPlaceholder
        && !node.IsDirectory
        && !string.IsNullOrWhiteSpace(node.FullPath);

    [RelayCommand(CanExecute = nameof(CanOpenWorkspaceTreeNodeInExplorer))]
    private void OpenWorkspaceTreeNodeInExplorer(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanOpenWorkspaceTreeNodeInExplorer(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(node.FullPath);
            // ????????????????????
            var targetPath = node.IsDirectory ? fullPath : Path.GetDirectoryName(fullPath);
            if (targetPath is null)
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _notifier.Warning("Shell_OpenFolderFailedTitle", "Shell_OpenFolderFailedMessage", exception.Message);
        }
    }

    private bool CanOpenWorkspaceTreeNodeInExplorer(WorkspaceTreeNodeViewModel? node) =>
        node is not null
        && !node.IsPlaceholder
        && !node.IsExpanderPlaceholder
        && !string.IsNullOrWhiteSpace(node.FullPath);

    [RelayCommand]
    private async Task SaveActiveEditorAsync()
    {
        if (FileEditor.ActiveDocument is null)
        {
            return;
        }

        await FileEditor.SaveDocumentAsync(FileEditor.ActiveDocument).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CloseEditorTab(EditorDocumentViewModel? document) =>
        await FileEditor.CloseTabAsync(document).ConfigureAwait(true);

    public Task<bool> ConfirmCloseEditorTabsAsync() => FileEditor.TryCloseAllTabsAsync();

    public void UpdateEditorPaneWidth(double width) =>
        _layout.UpdateEditorPaneWidth(width);

    [RelayCommand(CanExecute = nameof(CanDeleteWorkspaceItem))]
    private void DeleteWorkspaceItem(WorkspaceTreeNodeViewModel? node)
    {
        if (!CanDeleteWorkspaceItem(node) || node is null || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        var path = Path.GetFullPath(node.FullPath);
        var kind = node.IsDirectory ? _loc["Shell_FolderKind"] : _loc["Shell_FileKind"];
        var messageKey = node.IsDirectory ? "Shell_DeleteFolderMessage" : "Shell_DeleteFileMessage";

        if (!_notifier.ConfirmYesNo("Shell_DeleteNodeTitle", messageKey, node.Name))
        {
            return;
        }

        try
        {
            if (node.IsDirectory)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            else
            {
                ShowShellToast(_loc["Shell_TargetMissing"], ShellToastKind.Error);
                Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
                return;
            }

            RefreshAtCompletionSources();
            Sidebar.RefreshWorkspaceTree(_session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
            ShowShellToast(_loc.Format("Shell_DeleteSuccess", kind, node.Name), ShellToastKind.Success);
        }
        catch (Exception exception)
        {
            _notifier.Warning("Shell_DeleteFailedTitle", "Shell_DeleteFailedMessage", node.Name, exception.Message);
            ShowShellToast(_loc.Format("Shell_DeleteFailedStatus", exception.Message), ShellToastKind.Error);
        }
    }

    private bool CanDeleteWorkspaceItem(WorkspaceTreeNodeViewModel? node) =>
        node is not null
        && !node.IsPlaceholder
        && !node.IsExpanderPlaceholder
        && !string.IsNullOrWhiteSpace(node.FullPath)
        && WorkspaceSessionBridge.TryGetActiveWorkspaceRoot(_session, out var root)
        && WorkspaceSessionBridge.IsPathUnderWorkspace(root, node.FullPath)
        && !WorkspaceSessionBridge.IsWorkspaceRootPath(root, node.FullPath);
}
