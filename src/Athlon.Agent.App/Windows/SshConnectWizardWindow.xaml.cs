using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;
using Microsoft.Win32;

namespace Athlon.Agent.App.Windows;

public partial class SshConnectWizardWindow : Window
{
    private readonly SshWorkspaceConnectionService _connectionService;
    private readonly ICredentialStore _credentialStore;
    private readonly IUserNotifier _notifier;
    private readonly ILocalizationService _loc;
    private readonly WorkspaceSettings _workspace;
    private int _step = 1;

    public SshConnectWizardWindow(
        SshWorkspaceConnectionService connectionService,
        ICredentialStore credentialStore,
        IUserNotifier notifier,
        ILocalizationService localization,
        WorkspaceSettings? existing = null)
    {
        InitializeComponent();
        _connectionService = connectionService;
        _credentialStore = credentialStore;
        _notifier = notifier;
        _loc = localization;
        _workspace = existing ?? new WorkspaceSettings
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = WorkspaceKinds.Ssh,
            Ssh = new SshWorkspaceSettings()
        };

        if (_workspace.Ssh is null)
        {
            _workspace.Ssh = new SshWorkspaceSettings();
        }

        HostBox.Text = BuildHostHint(_workspace);
        UsernameBox.Text = _workspace.Ssh.Username;
        PortBox.Text = (_workspace.Ssh.Port is > 0 ? _workspace.Ssh.Port : 22).ToString();
        PrivateKeyPathBox.Text = _workspace.Ssh.PrivateKeyPath ?? string.Empty;
        NameBox.Text = _workspace.Name;
        RemoteRootBox.Text = string.IsNullOrWhiteSpace(_workspace.RootPath) ? "/home" : _workspace.RootPath;

        var authMode = _workspace.Ssh.AuthMode ?? "password";
        foreach (var item in AuthModeCombo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), authMode, StringComparison.OrdinalIgnoreCase))
            {
                AuthModeCombo.SelectedItem = item;
                break;
            }
        }

        UpdateAuthPanels();
        ShowStep(1);
        Loaded += (_, _) => HostBox.Focus();
    }

    public WorkspaceSettings? ResultWorkspace { get; private set; }

    private static string BuildHostHint(WorkspaceSettings workspace)
    {
        if (workspace.Ssh is null || string.IsNullOrWhiteSpace(workspace.Ssh.Host))
        {
            return string.Empty;
        }

        var user = workspace.Ssh.Username?.Trim() ?? string.Empty;
        var host = workspace.Ssh.Host.Trim();
        return string.IsNullOrWhiteSpace(user) ? host : $"{user}@{host}";
    }

    private void ShowStep(int step)
    {
        _step = step;
        HeaderText.Text = _loc.Format("Shell_SshWizardStepHeader", step);
        Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = string.Empty;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            ShowStep(_step - 1);
        }
    }

    private void Step1Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseHost(HostBox.Text, out var username, out var host, out var port, out _))
        {
            StatusText.Text = _loc["Shell_SshWizardHostRequired"];
            return;
        }

        _workspace.Ssh!.Host = host;
        _workspace.Ssh.Port = port;
        if (!string.IsNullOrWhiteSpace(username))
        {
            UsernameBox.Text = username;
            _workspace.Ssh.Username = username;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Text = string.IsNullOrWhiteSpace(username) ? host : $"{username}@{host}";
        }

        ShowStep(2);
        UsernameBox.Focus();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureAuth(out var error))
        {
            StatusText.Text = error;
            return;
        }

        ShowStep(3);
        RemoteRootBox.Focus();
    }

    private async void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureAuth(out var error) || !TryCaptureRoot(out error))
        {
            StatusText.Text = error;
            return;
        }

        StatusText.Text = _loc["Shell_SshTesting"];
        try
        {
            await PersistSecretsAsync().ConfigureAwait(true);
            await _connectionService.TestConnectionAsync(_workspace).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = _loc.Format("Shell_SshTestFailed", ex.Message);
            return;
        }

        ResultWorkspace = _workspace;
        DialogResult = true;
    }

    private void AuthModeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthPanels();

    private void UpdateAuthPanels()
    {
        // SelectionChanged can fire during InitializeComponent before later named panels exist.
        if (PasswordPanel is null || PrivateKeyPanel is null)
        {
            return;
        }

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

    private void OpenSshConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh",
                "config");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(path))
            {
                File.WriteAllText(path, "# SSH config\n");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notifier.WarningText("Common_Prompt", ex.Message);
        }
    }

    private bool TryCaptureAuth(out string error)
    {
        error = string.Empty;
        var username = UsernameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username))
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
        }
        else if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            error = _loc["Shell_SshPasswordRequired"];
            return false;
        }

        _workspace.Ssh!.Username = username;
        _workspace.Ssh.Port = port;
        _workspace.Ssh.AuthMode = authMode;
        _workspace.Ssh.PrivateKeyPath = string.Equals(authMode, "privateKey", StringComparison.OrdinalIgnoreCase)
            ? PrivateKeyPathBox.Text.Trim()
            : null;
        return true;
    }

    private bool TryCaptureRoot(out string error)
    {
        error = string.Empty;
        var name = NameBox.Text.Trim();
        var root = RemoteRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(root))
        {
            error = _loc["Shell_SshFieldsRequired"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(_workspace.Id))
        {
            _workspace.Id = Guid.NewGuid().ToString("N");
        }

        _workspace.Name = name;
        _workspace.Kind = WorkspaceKinds.Ssh;
        _workspace.RootPath = RemotePathNormalizer.NormalizeRoot(root);
        return true;
    }

    private async Task PersistSecretsAsync()
    {
        var authMode = _workspace.Ssh?.AuthMode ?? "password";
        if (string.Equals(authMode, "privateKey", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(PassphraseBox.Password))
            {
                await _credentialStore
                    .SaveSecretAsync(SshWorkspaceSettings.KeyPassphraseSecretName(_workspace.Id), PassphraseBox.Password)
                    .ConfigureAwait(true);
            }

            return;
        }

        await _credentialStore
            .SaveSecretAsync(SshWorkspaceSettings.PasswordSecretName(_workspace.Id), PasswordBox.Password)
            .ConfigureAwait(true);
    }

    internal static bool TryParseHost(string raw, out string username, out string host, out int port, out string error)
    {
        username = string.Empty;
        host = string.Empty;
        port = 22;
        error = string.Empty;

        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "host-required";
            return false;
        }

        // user@host[:port] or host[:port]
        var at = text.LastIndexOf('@');
        var hostPart = text;
        if (at >= 0)
        {
            username = text[..at].Trim();
            hostPart = text[(at + 1)..].Trim();
        }

        if (hostPart.StartsWith('[') && hostPart.Contains(']'))
        {
            // [ipv6]:port
            var end = hostPart.IndexOf(']');
            host = hostPart[1..end];
            var rest = hostPart[(end + 1)..];
            if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p) && p is > 0 and <= 65535)
            {
                port = p;
            }
        }
        else
        {
            var colon = hostPart.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hostPart[(colon + 1)..], out var p) && p is > 0 and <= 65535)
            {
                host = hostPart[..colon].Trim();
                port = p;
            }
            else
            {
                host = hostPart;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "host-required";
            return false;
        }

        return true;
    }
}
