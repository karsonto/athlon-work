using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

/// <summary>Thin delegates to <see cref="SshWorkspaceCoordinator"/> for XAML-facing workspace APIs.</summary>
public partial class MainShellViewModel
{
    [RelayCommand]
    private async Task WorkspacePanelActionAsync() =>
        await _sshWorkspace.ConfigureLocalWorkspaceAsync().ConfigureAwait(true);

    public ContextMenu BuildRunOnMenu() => _sshWorkspace.BuildRunOnMenu();

    [RelayCommand]
    private async Task ConfigureWorkspaceAsync() =>
        await _sshWorkspace.ConfigureLocalWorkspaceAsync().ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanDownloadWorkspaceTreeNode))]
    private Task DownloadWorkspaceTreeNodeAsync(WorkspaceTreeNodeViewModel? node) =>
        _sshWorkspace.DownloadWorkspaceTreeNodeAsync(node);

    private bool CanDownloadWorkspaceTreeNode(WorkspaceTreeNodeViewModel? node) =>
        _sshWorkspace.CanDownloadWorkspaceTreeNode(node);
}
