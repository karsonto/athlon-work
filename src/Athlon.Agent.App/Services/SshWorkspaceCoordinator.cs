using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Ssh;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace Athlon.Agent.App.Services;

/// <summary>SSH workspace configuration, run-on menu, remote transfer, and workspace tree sync.</summary>
public sealed class SshWorkspaceCoordinator
{
    private readonly IFileStorageService _storage;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly AppSettings _appSettings;
    private readonly ILocalizationService _loc;
    private readonly IUserNotifier _notifier;
    private readonly SshWorkspaceConnectionService _sshConnection;
    private readonly ICredentialStore _credentialStore;
    private readonly ISshWorkspaceClient _sshClient;
    private readonly SshWorkspaceTransferService _sshTransfer;

    private Func<AgentSession>? _getSession;
    private Action<AgentSession>? _setSession;
    private Func<bool>? _hasSessionWorkspace;
    private Func<FileEditorViewModel>? _getFileEditor;
    private Func<ContextSidebarViewModel>? _getSidebar;
    private Action<string, ShellToastKind>? _showShellToast;
    private Func<Task>? _saveCurrentSessionIfNeededAsync;
    private Func<Task>? _applySessionWorkspaceShellAsync;
    private Action? _notifyRunOnPropertiesChanged;

    public SshWorkspaceCoordinator(
        IFileStorageService storage,
        IActiveWorkspaceContext workspaceContext,
        AppSettings appSettings,
        ILocalizationService localization,
        IUserNotifier notifier,
        SshWorkspaceConnectionService sshConnection,
        ICredentialStore credentialStore,
        ISshWorkspaceClient sshClient,
        SshWorkspaceTransferService sshTransfer)
    {
        _storage = storage;
        _workspaceContext = workspaceContext;
        _appSettings = appSettings;
        _loc = localization;
        _notifier = notifier;
        _sshConnection = sshConnection;
        _credentialStore = credentialStore;
        _sshClient = sshClient;
        _sshTransfer = sshTransfer;
    }

    public void Configure(
        Func<AgentSession> getSession,
        Action<AgentSession> setSession,
        Func<bool> hasSessionWorkspace,
        Func<FileEditorViewModel> getFileEditor,
        Func<ContextSidebarViewModel> getSidebar,
        Action<string, ShellToastKind> showShellToast,
        Func<Task> saveCurrentSessionIfNeededAsync,
        Func<Task> applySessionWorkspaceShellAsync,
        Action notifyRunOnPropertiesChanged)
    {
        _getSession = getSession;
        _setSession = setSession;
        _hasSessionWorkspace = hasSessionWorkspace;
        _getFileEditor = getFileEditor;
        _getSidebar = getSidebar;
        _showShellToast = showShellToast;
        _saveCurrentSessionIfNeededAsync = saveCurrentSessionIfNeededAsync;
        _applySessionWorkspaceShellAsync = applySessionWorkspaceShellAsync;
        _notifyRunOnPropertiesChanged = notifyRunOnPropertiesChanged;
    }

    public void SetDefaultSession(string sessionId) =>
        _sshConnection.SetDefaultSession(sessionId);

    public Task DisconnectSessionAsync(string sessionId) =>
        _sshConnection.DisconnectSessionAsync(sessionId);

    public Task DisconnectAllAsync(CancellationToken cancellationToken) =>
        _sshConnection.DisconnectAllAsync(cancellationToken);

    public async Task SyncSshConnectionIfNeededAsync()
    {
        var session = _getSession!();
        if (_workspaceContext.Kind == WorkspaceKind.Ssh
            && !string.IsNullOrWhiteSpace(session.ActiveWorkspaceId))
        {
            try
            {
                await _sshConnection.SyncAsync(session, _appSettings).ConfigureAwait(true);
            }
            catch
            {
                // Connection errors surface when tools run or when configuring workspace.
            }
        }
    }

    public string ResolveActiveWorkspaceName()
    {
        if (!_hasSessionWorkspace!())
        {
            return _loc["Shell_NoWorkspace"];
        }

        if (_workspaceContext.Kind == WorkspaceKind.Ssh)
        {
            var match = WorkspaceSessionResolver.FindMatch(_getSession!(), _appSettings);
            if (match is not null)
            {
                return FormatRemoteLabel(match);
            }
        }

        return _workspaceContext.DisplayName ?? _loc["Shell_NoWorkspace"];
    }

    public string GetRunOnDisplayName()
    {
        if (!_hasSessionWorkspace!())
        {
            return _loc["Shell_ChooseWorkspace"];
        }

        if (_workspaceContext.Kind == WorkspaceKind.Ssh)
        {
            var match = WorkspaceSessionResolver.FindMatch(_getSession!(), _appSettings);
            if (match is not null)
            {
                return FormatRemoteLabel(match);
            }

            return _workspaceContext.DisplayName ?? _loc["Shell_RunOnRemote"];
        }

        return _workspaceContext.DisplayName ?? _loc["Shell_RunOnThisPc"];
    }

    public async Task RefreshWorkspaceTreeAsync()
    {
        var session = _getSession!();
        var sidebar = _getSidebar!();
        if (_workspaceContext.Kind == WorkspaceKind.Ssh
            && _sshClient.IsConnected
            && !string.IsNullOrWhiteSpace(_workspaceContext.RootPath))
        {
            try
            {
                var entries = new List<SshEntry>();
                await foreach (var entry in _sshClient.ListAsync(_workspaceContext.RootPath).ConfigureAwait(true))
                {
                    if (SshWorkspaceToolHelper.ShouldIgnore(entry.FullPath, _workspaceContext.IgnorePatterns))
                    {
                        continue;
                    }

                    entries.Add(entry);
                    if (entries.Count >= 200)
                    {
                        break;
                    }
                }

                sidebar.RefreshRemoteWorkspaceTree(
                    _workspaceContext.RootPath,
                    _workspaceContext.DisplayName ?? _workspaceContext.RootPath,
                    entries,
                    _workspaceContext.IgnorePatterns);
                return;
            }
            catch
            {
                sidebar.RefreshWorkspaceTree(null, _workspaceContext.IgnorePatterns);
                return;
            }
        }

        sidebar.RefreshWorkspaceTree(session.ActiveWorkspace, _workspaceContext.IgnorePatterns);
    }

    public ContextMenu BuildRunOnMenu() =>
        new RunOnMenuBuilder(new RunOnMenuBuilder.RunOnMenuContext(
            _loc,
            _appSettings,
            _workspaceContext,
            () => _getSession!(),
            () => _hasSessionWorkspace!(),
            ScheduleUi,
            ConfigureLocalWorkspaceAsync,
            ApplyConfiguredSshWorkspaceAsync,
            ConfigureSshWorkspaceAsync,
            RemoveSessionWorkspaceAsync,
            DeleteSshConnectionAsync)).Build();

    public async Task ConfigureLocalWorkspaceAsync()
    {
        var session = _getSession!();
        var dialog = new OpenFolderDialog
        {
            Title = _loc["Shell_SelectWorkspace"],
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace)
            && _workspaceContext.Kind == WorkspaceKind.Local
            && Directory.Exists(session.ActiveWorkspace))
        {
            dialog.InitialDirectory = session.ActiveWorkspace;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _sshConnection.DisconnectSessionAsync(session.Id).ConfigureAwait(true);

        var folderName = new DirectoryInfo(dialog.FolderName).Name;
        _setSession!(session.WithWorkspace(dialog.FolderName, workspaceId: null));
        await _applySessionWorkspaceShellAsync!().ConfigureAwait(true);
        await _saveCurrentSessionIfNeededAsync!().ConfigureAwait(true);
        _showShellToast!(_loc.Format("Shell_WorkspaceStatus", folderName), ShellToastKind.Success);
    }

    public async Task RemoveSessionWorkspaceAsync()
    {
        if (!_hasSessionWorkspace!())
        {
            return;
        }

        if (!await _getFileEditor!().TryCloseAllTabsAsync().ConfigureAwait(true))
        {
            return;
        }

        var session = _getSession!();
        await _sshConnection.DisconnectSessionAsync(session.Id).ConfigureAwait(true);
        _setSession!(session.WithWorkspace(null, workspaceId: null));
        await _applySessionWorkspaceShellAsync!().ConfigureAwait(true);
        await _saveCurrentSessionIfNeededAsync!().ConfigureAwait(true);
        _showShellToast!(_loc["Shell_WorkspaceRemoved"], ShellToastKind.Success);
    }

    public bool CanAcceptRemoteFileDrop =>
        _workspaceContext.Kind == WorkspaceKind.Ssh
        && _sshClient.IsConnected
        && !string.IsNullOrWhiteSpace(_workspaceContext.RootPath);

    public bool CanDownloadWorkspaceTreeNode(WorkspaceTreeNodeViewModel? node) =>
        node is not null
        && node.IsRemote
        && !node.IsPlaceholder
        && !node.IsExpanderPlaceholder
        && !string.IsNullOrWhiteSpace(node.FullPath)
        && _workspaceContext.Kind == WorkspaceKind.Ssh
        && _sshClient.IsConnected;

    public async Task DownloadWorkspaceTreeNodeAsync(WorkspaceTreeNodeViewModel? node)
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
                _showShellToast!(_loc.Format("Shell_DownloadCompleteStatus", downloaded, skipped), ShellToastKind.Success);
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
                    _showShellToast!(_loc["Shell_DownloadSkippedStatus"], ShellToastKind.Success);
                    return;
                }

                await _sshTransfer.DownloadFileAsync(node.FullPath, saveDialog.FileName).ConfigureAwait(true);
                _showShellToast!(_loc.Format("Shell_DownloadFileSuccess", node.Name), ShellToastKind.Success);
            }
        }
        catch (Exception exception)
        {
            _notifier.Warning("Shell_DownloadFailedTitle", "Shell_DownloadFailedMessage", node.Name, exception.Message);
            _showShellToast!(_loc.Format("Shell_DownloadFailedStatus", exception.Message), ShellToastKind.Error);
        }
    }

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
            _showShellToast!(_loc.Format("Shell_UploadCompleteStatus", uploaded, skipped), ShellToastKind.Success);
        }
        catch (Exception exception)
        {
            _notifier.Warning("Shell_UploadFailedTitle", "Shell_UploadFailedMessage", exception.Message);
            _showShellToast!(_loc.Format("Shell_UploadFailedStatus", exception.Message), ShellToastKind.Error);
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

    public static string FormatRemoteLabel(WorkspaceSettings workspace)
    {
        var ssh = workspace.Ssh;
        var host = ssh?.Host?.Trim();
        var name = workspace.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(name))
        {
            return $"{host}:{name}";
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (ssh is null)
        {
            return workspace.RootPath;
        }

        return string.IsNullOrWhiteSpace(ssh.Username)
            ? (string.IsNullOrWhiteSpace(host) ? workspace.RootPath : host)
            : $"{ssh.Username}@{ssh.Host}";
    }

    private async Task ConfigureSshWorkspaceAsync()
    {
        var dialog = new SshConnectWizardWindow(
            _sshConnection,
            _credentialStore,
            _notifier,
            _loc)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true || dialog.ResultWorkspace is null)
        {
            return;
        }

        await ApplyConfiguredSshWorkspaceAsync(dialog.ResultWorkspace).ConfigureAwait(true);
    }

    private async Task ApplyConfiguredSshWorkspaceAsync(WorkspaceSettings configured)
    {
        var session = _getSession!();
        var existing = _appSettings.Workspaces.FindIndex(item =>
            string.Equals(item.Id, configured.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _appSettings.Workspaces[existing] = configured;
        }
        else
        {
            _appSettings.Workspaces.Add(configured);
        }

        await _storage.SaveSettingsAsync(_appSettings).ConfigureAwait(true);
        try
        {
            await _sshConnection.SyncAsync(
                session.WithWorkspace(configured.RootPath, configured.Id),
                _appSettings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notifier.WarningText("Common_Prompt", _loc.Format("Shell_SshTestFailed", ex.Message));
            return;
        }

        _setSession!(session.WithWorkspace(configured.RootPath, configured.Id));
        await _applySessionWorkspaceShellAsync!().ConfigureAwait(true);
        await _saveCurrentSessionIfNeededAsync!().ConfigureAwait(true);
        _showShellToast!(_loc.Format("Shell_SshConnectedStatus", FormatRemoteLabel(configured)), ShellToastKind.Success);
    }

    private async Task DeleteSshConnectionAsync(WorkspaceSettings workspace)
    {
        var label = FormatRemoteLabel(workspace);
        if (!_notifier.ConfirmYesNo("Shell_SshDeleteConnectionTitle", "Shell_SshDeleteConnectionConfirm", label))
        {
            return;
        }

        var session = _getSession!();
        var isActive = _hasSessionWorkspace!()
            && _workspaceContext.Kind == WorkspaceKind.Ssh
            && !string.IsNullOrWhiteSpace(session.ActiveWorkspaceId)
            && string.Equals(workspace.Id, session.ActiveWorkspaceId, StringComparison.OrdinalIgnoreCase);

        if (isActive)
        {
            if (!await _getFileEditor!().TryCloseAllTabsAsync().ConfigureAwait(true))
            {
                return;
            }

            await _sshConnection.DisconnectSessionAsync(session.Id).ConfigureAwait(true);
            _setSession!(session.WithWorkspace(null, workspaceId: null));
            await _applySessionWorkspaceShellAsync!().ConfigureAwait(true);
            await _saveCurrentSessionIfNeededAsync!().ConfigureAwait(true);
        }

        _appSettings.Workspaces.RemoveAll(item =>
            string.Equals(item.Id, workspace.Id, StringComparison.OrdinalIgnoreCase));

        try
        {
            await _credentialStore
                .DeleteSecretAsync(SshWorkspaceSettings.PasswordSecretName(workspace.Id))
                .ConfigureAwait(true);
            await _credentialStore
                .DeleteSecretAsync(SshWorkspaceSettings.KeyPassphraseSecretName(workspace.Id))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        await _storage.SaveSettingsAsync(_appSettings).ConfigureAwait(true);
        _showShellToast!(_loc.Format("Shell_SshConnectionDeleted", label), ShellToastKind.Success);
        _notifyRunOnPropertiesChanged!();
    }

    private static void ScheduleUi(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _ = action();
            return;
        }

        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }, DispatcherPriority.ApplicationIdle);
    }
}
