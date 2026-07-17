using System.IO;
using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;
using Microsoft.Win32;

namespace Athlon.Agent.App.Windows;

public partial class SshWorkspaceEditWindow : Window
{
    private readonly SshWorkspaceConnectionService _connectionService;
    private readonly ICredentialStore _credentialStore;
    private readonly IUserNotifier _notifier;
    private readonly ILocalizationService _loc;
    private readonly WorkspaceSettings _workspace;

    public SshWorkspaceEditWindow(
        WorkspaceSettings workspace,
        SshWorkspaceConnectionService connectionService,
        ICredentialStore credentialStore,
        IUserNotifier notifier,
        ILocalizationService localization)
    {
        InitializeComponent();
        _workspace = workspace;
        _connectionService = connectionService;
        _credentialStore = credentialStore;
        _notifier = notifier;
        _loc = localization;

        NameBox.Text = workspace.Name;
        HostBox.Text = workspace.Ssh?.Host ?? string.Empty;
        PortBox.Text = (workspace.Ssh?.Port is > 0 ? workspace.Ssh.Port : 22).ToString();
        UsernameBox.Text = workspace.Ssh?.Username ?? string.Empty;
        RemoteRootBox.Text = string.IsNullOrWhiteSpace(workspace.RootPath) ? "/home" : workspace.RootPath;
        PrivateKeyPathBox.Text = workspace.Ssh?.PrivateKeyPath ?? string.Empty;

        var authMode = workspace.Ssh?.AuthMode ?? "password";
        foreach (var item in AuthModeCombo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), authMode, StringComparison.OrdinalIgnoreCase))
            {
                AuthModeCombo.SelectedItem = item;
                break;
            }
        }

        UpdateAuthPanels();
    }

    public WorkspaceSettings? ResultWorkspace { get; private set; }

    public string? Password { get; private set; }

    public string? Passphrase { get; private set; }

    private void AuthModeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthPanels();

    private void UpdateAuthPanels()
    {
        var mode = (AuthModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "password";
        var isKey = string.Equals(mode, "privateKey", StringComparison.OrdinalIgnoreCase);
        PasswordPanel.Visibility = isKey ? Visibility.Collapsed : Visibility.Visible;
        PrivateKeyPanel.Visibility = isKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _loc["Shell_SshPrivateKeyPath"],
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            PrivateKeyPathBox.Text = dialog.FileName;
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildWorkspace(out var workspace, out var password, out var passphrase, out var error))
        {
            StatusText.Text = error;
            return;
        }

        StatusText.Text = _loc["Shell_SshTesting"];
        try
        {
            await PersistSecretsAsync(workspace, password, passphrase).ConfigureAwait(true);
            await _connectionService.TestConnectionAsync(workspace).ConfigureAwait(true);
            StatusText.Text = _loc["Shell_SshTestSucceeded"];
        }
        catch (Exception ex)
        {
            StatusText.Text = _loc.Format("Shell_SshTestFailed", ex.Message);
        }
    }

    private async void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildWorkspace(out var workspace, out var password, out var passphrase, out var error))
        {
            _notifier.WarningText("Common_Prompt", error);
            return;
        }

        try
        {
            await PersistSecretsAsync(workspace, password, passphrase).ConfigureAwait(true);
            await _connectionService.TestConnectionAsync(workspace).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notifier.WarningText("Common_Prompt", _loc.Format("Shell_SshTestFailed", ex.Message));
            return;
        }

        ResultWorkspace = workspace;
        Password = password;
        Passphrase = passphrase;
        DialogResult = true;
    }

    private bool TryBuildWorkspace(
        out WorkspaceSettings workspace,
        out string? password,
        out string? passphrase,
        out string error)
    {
        workspace = _workspace;
        password = null;
        passphrase = null;
        error = string.Empty;

        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var remoteRoot = RemoteRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = _loc["Shell_SshNameRequired"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(remoteRoot))
        {
            error = _loc["Shell_SshFieldsRequired"];
            return false;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port <= 0 || port > 65535)
        {
            error = _loc["Shell_SshPortInvalid"];
            return false;
        }

        var authMode = (AuthModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "password";
        if (string.Equals(authMode, "privateKey", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(PrivateKeyPathBox.Text) || !File.Exists(PrivateKeyPathBox.Text.Trim()))
            {
                error = _loc["Shell_SshPrivateKeyRequired"];
                return false;
            }

            passphrase = string.IsNullOrEmpty(PassphraseBox.Password) ? null : PassphraseBox.Password;
        }
        else
        {
            if (string.IsNullOrEmpty(PasswordBox.Password))
            {
                error = _loc["Shell_SshPasswordRequired"];
                return false;
            }

            password = PasswordBox.Password;
        }

        if (string.IsNullOrWhiteSpace(workspace.Id))
        {
            workspace.Id = Guid.NewGuid().ToString("N");
        }

        workspace.Name = name;
        workspace.Kind = WorkspaceKinds.Ssh;
        workspace.RootPath = RemotePathNormalizer.NormalizeRoot(remoteRoot);
        workspace.Ssh = new SshWorkspaceSettings
        {
            Host = host,
            Port = port,
            Username = username,
            AuthMode = authMode,
            PrivateKeyPath = string.Equals(authMode, "privateKey", StringComparison.OrdinalIgnoreCase)
                ? PrivateKeyPathBox.Text.Trim()
                : null
        };
        return true;
    }

    private async Task PersistSecretsAsync(WorkspaceSettings workspace, string? password, string? passphrase)
    {
        if (!string.IsNullOrEmpty(password))
        {
            await _credentialStore
                .SaveSecretAsync(SshWorkspaceSettings.PasswordSecretName(workspace.Id), password)
                .ConfigureAwait(true);
        }

        if (!string.IsNullOrEmpty(passphrase))
        {
            await _credentialStore
                .SaveSecretAsync(SshWorkspaceSettings.KeyPassphraseSecretName(workspace.Id), passphrase)
                .ConfigureAwait(true);
        }
    }
}
