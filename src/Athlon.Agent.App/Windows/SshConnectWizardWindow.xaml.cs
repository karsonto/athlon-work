using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;
using Microsoft.Win32;

namespace Athlon.Agent.App.Windows;

public partial class SshConnectWizardWindow : Window
{
    private readonly SshWorkspaceConnectionService _connectionService;
    private readonly ISshWorkspaceClient _sshClient;
    private readonly ICredentialStore _credentialStore;
    private readonly IUserNotifier _notifier;
    private readonly ILocalizationService _loc;
    private readonly WorkspaceSettings _workspace;
    private readonly ObservableCollection<BrowseNode> _treeRoots = new();
    private int _step = 1;
    private bool _browseConnected;
    private string? _selectedRemotePath;
    private string? _preferredBrowseRoot;
    private bool _nameTouchedByUser;
    private bool _suppressNameTouch;

    public SshConnectWizardWindow(
        SshWorkspaceConnectionService connectionService,
        ISshWorkspaceClient sshClient,
        ICredentialStore credentialStore,
        IUserNotifier notifier,
        ILocalizationService localization,
        WorkspaceSettings? existing = null)
    {
        InitializeComponent();
        _connectionService = connectionService;
        _sshClient = sshClient;
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
        _nameTouchedByUser = !string.IsNullOrWhiteSpace(_workspace.Name);
        SelectedPathBox.Text = string.Empty;
        RemoteTree.ItemsSource = _treeRoots;

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
        NameBox.TextChanged += (_, _) =>
        {
            if (!_suppressNameTouch)
            {
                _nameTouchedByUser = true;
            }
        };
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
        if (step != 3)
        {
            StatusText.Text = string.Empty;
        }
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void Window_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DialogResult == true || !_browseConnected)
        {
            return;
        }

        try
        {
            await _connectionService.DisconnectAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore disconnect failures while closing
        }
    }

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

        if (!_nameTouchedByUser || string.IsNullOrWhiteSpace(NameBox.Text))
        {
            SetNameBox(string.IsNullOrWhiteSpace(username) ? host : $"{username}@{host}");
            _nameTouchedByUser = false;
        }

        ShowStep(2);
        UsernameBox.Focus();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureAuth(out var error))
        {
            StatusText.Text = error;
            return;
        }

        if (string.IsNullOrWhiteSpace(_workspace.Id))
        {
            _workspace.Id = Guid.NewGuid().ToString("N");
        }

        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        StatusText.Text = _loc["Shell_SshWizardConnecting"];
        try
        {
            await PersistSecretsAsync().ConfigureAwait(true);
            _preferredBrowseRoot = string.IsNullOrWhiteSpace(_workspace.RootPath) || _workspace.RootPath == "/"
                ? null
                : _workspace.RootPath;
            // Browse from filesystem root; final workspace root is chosen in step 3.
            _workspace.RootPath = "/";
            await _connectionService.TestConnectionAsync(_workspace).ConfigureAwait(true);
            _browseConnected = true;
            StatusText.Text = _loc["Shell_SshWizardLoadingTree"];
            await LoadRemoteTreeAsync().ConfigureAwait(true);
            ShowStep(3);
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _browseConnected = false;
            StatusText.Text = _loc.Format("Shell_SshTestFailed", ex.Message);
        }
        finally
        {
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
        }
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureRoot(out var error))
        {
            StatusText.Text = error;
            return;
        }

        ResultWorkspace = _workspace;
        DialogResult = true;
    }

    private async Task LoadRemoteTreeAsync()
    {
        _treeRoots.Clear();
        _selectedRemotePath = null;
        SelectedPathBox.Text = string.Empty;

        var startPath = await ResolveStartPathAsync().ConfigureAwait(true);
        var root = await CreateDirectoryNodeAsync(startPath, displayName: startPath).ConfigureAwait(true);
        root.IsExpanded = true;
        _treeRoots.Add(root);
        await LoadChildrenAsync(root).ConfigureAwait(true);
    }

    private async Task<string> ResolveStartPathAsync()
    {
        var preferred = string.IsNullOrWhiteSpace(_preferredBrowseRoot)
            ? null
            : RemotePathNormalizer.NormalizeRoot(_preferredBrowseRoot);
        if (!string.IsNullOrWhiteSpace(preferred)
            && preferred != "/"
            && await _sshClient.FileExistsAsync(preferred).ConfigureAwait(true))
        {
            return preferred;
        }

        var username = _workspace.Ssh?.Username?.Trim();
        if (!string.IsNullOrWhiteSpace(username))
        {
            var home = RemotePathNormalizer.Combine("/", "home/" + username);
            if (await _sshClient.FileExistsAsync(home).ConfigureAwait(true))
            {
                return home;
            }
        }

        return "/";
    }

    private async Task<BrowseNode> CreateDirectoryNodeAsync(string fullPath, string? displayName = null)
    {
        var node = new BrowseNode(
            displayName ?? RemotePathNormalizer.GetFileName(fullPath),
            fullPath,
            isDirectory: true);

        // Placeholder so the expander arrow appears before children are loaded.
        node.Children.Add(BrowseNode.CreatePlaceholder());
        return await Task.FromResult(node).ConfigureAwait(true);
    }

    private async Task LoadChildrenAsync(BrowseNode node)
    {
        if (!node.IsDirectory || node.ChildrenLoaded)
        {
            return;
        }

        node.ChildrenLoaded = true;
        node.Children.Clear();

        try
        {
            var directories = new List<SshEntry>();
            await foreach (var entry in _sshClient.ListAsync(node.FullPath).ConfigureAwait(true))
            {
                if (entry.IsDirectory)
                {
                    directories.Add(entry);
                }
            }

            foreach (var entry in directories.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                node.Children.Add(await CreateDirectoryNodeAsync(entry.FullPath, entry.Name).ConfigureAwait(true));
            }
        }
        catch (Exception ex)
        {
            node.Children.Add(BrowseNode.CreatePlaceholder(_loc.Format("Shell_SshTestFailed", ex.Message)));
        }
    }

    private async void RemoteTreeItem_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: BrowseNode node })
        {
            return;
        }

        e.Handled = true;
        await LoadChildrenAsync(node).ConfigureAwait(true);
    }

    private void RemoteTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not BrowseNode { IsDirectory: true, IsPlaceholder: false } node)
        {
            return;
        }

        _selectedRemotePath = node.FullPath;
        SelectedPathBox.Text = node.FullPath;

        if (!_nameTouchedByUser)
        {
            var leaf = RemotePathNormalizer.GetFileName(node.FullPath);
            if (!string.IsNullOrWhiteSpace(leaf) && leaf != "/")
            {
                SetNameBox(leaf);
            }
        }
    }

    private void SetNameBox(string value)
    {
        _suppressNameTouch = true;
        try
        {
            NameBox.Text = value;
        }
        finally
        {
            _suppressNameTouch = false;
        }
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
        var root = (_selectedRemotePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            error = _loc["Shell_SshWizardSelectFolder"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = _loc["Shell_SshNameRequired"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(_workspace.Id))
        {
            _workspace.Id = Guid.NewGuid().ToString("N");
        }

        _workspace.Name = name;
        _workspace.Kind = WorkspaceKinds.Ssh;
        _workspace.RootPath = RemotePathNormalizer.NormalizeRoot(root);
        if (string.IsNullOrWhiteSpace(_workspace.RootPath))
        {
            _workspace.RootPath = "/";
        }

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

    private sealed class BrowseNode
    {
        public BrowseNode(string name, string fullPath, bool isDirectory, bool isPlaceholder = false)
        {
            Name = name;
            FullPath = fullPath;
            IsDirectory = isDirectory;
            IsPlaceholder = isPlaceholder;
        }

        public string Name { get; }
        public string FullPath { get; }
        public bool IsDirectory { get; }
        public bool IsPlaceholder { get; }
        public bool ChildrenLoaded { get; set; }
        public bool IsExpanded { get; set; }
        public ObservableCollection<BrowseNode> Children { get; } = new();

        public static BrowseNode CreatePlaceholder(string? message = null) =>
            new(message ?? string.Empty, string.Empty, isDirectory: false, isPlaceholder: true);
    }
}
