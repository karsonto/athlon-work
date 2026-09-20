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
/// Workspace surface: the run-on/SSH menu, remote-workspace configuration and session-
/// workspace plumbing. Split out of the shell orchestration file.
/// </summary>
public partial class MainShellViewModel
{
    [RelayCommand]
    private async Task WorkspacePanelActionAsync()
    {
        // Kept for compatibility; primary entry is the Run on menu.
        await ConfigureLocalWorkspaceAsync().ConfigureAwait(true);
    }

    public ContextMenu BuildRunOnMenu()
    {
        var menu = new ContextMenu
        {
            Style = Application.Current.TryFindResource("RunOnContextMenuStyle") as Style
        };

        var itemStyle = Application.Current.TryFindResource("RunOnMenuItemStyle") as Style;
        var isLocalActive = HasSessionWorkspace && _workspaceContext.Kind == WorkspaceKind.Local;
        var isSshActive = HasSessionWorkspace && _workspaceContext.Kind == WorkspaceKind.Ssh;
        var activeWorkspaceId = _session.ActiveWorkspaceId;

        menu.Items.Add(new MenuItem
        {
            Header = CreateRunOnSectionHeader(_loc["Shell_RunOn"]),
            IsEnabled = false,
            Style = itemStyle
        });

        var thisPc = new MenuItem
        {
            Header = CreateRunOnRowHeader(
                iconKind: PackIconKind.Laptop,
                text: _loc["Shell_RunOnThisPc"],
                trailing: isLocalActive ? RunOnTrailing.Check : RunOnTrailing.None),
            Style = itemStyle
        };
        thisPc.Click += (_, _) => ScheduleUi(ConfigureLocalWorkspaceAsync);
        menu.Items.Add(thisPc);

        var remote = new MenuItem
        {
            Header = CreateRunOnRowHeader(
                iconKind: PackIconKind.Monitor,
                text: _loc["Shell_RunOnRemoteConnection"],
                trailing: RunOnTrailing.Chevron),
            Style = itemStyle
        };

        var sshWorkspaces = _appSettings.Workspaces
            .Where(item => item.WorkspaceKind == WorkspaceKind.Ssh && item.Ssh is not null)
            .ToList();

        if (sshWorkspaces.Count == 0)
        {
            remote.Items.Add(new MenuItem
            {
                Header = CreateRunOnEmptyHint(_loc["Shell_RunOnNoRemotes"]),
                IsEnabled = false,
                Style = itemStyle
            });
        }
        else
        {
            foreach (var workspace in sshWorkspaces)
            {
                var selected = isSshActive
                    && !string.IsNullOrWhiteSpace(activeWorkspaceId)
                    && string.Equals(workspace.Id, activeWorkspaceId, StringComparison.OrdinalIgnoreCase);
                var item = new MenuItem
                {
                    Header = CreateSshConnectionRowHeader(workspace, selected),
                    Style = itemStyle,
                    Tag = workspace
                };
                item.Click += (_, _) =>
                {
                    if (item.Tag is WorkspaceSettings configured)
                    {
                        ScheduleUi(() => ApplyConfiguredSshWorkspaceAsync(configured));
                    }
                };
                remote.Items.Add(item);
            }
        }

        remote.Items.Add(CreateRunOnSeparator());

        var sshItem = new MenuItem
        {
            Header = CreateRunOnRowHeader(
                iconKind: PackIconKind.Plus,
                text: _loc["Shell_RunOnConnectSshAction"],
                trailing: RunOnTrailing.None),
            Style = itemStyle
        };
        sshItem.Click += (_, _) => ScheduleUi(ConfigureSshWorkspaceAsync);
        remote.Items.Add(sshItem);
        menu.Items.Add(remote);

        if (HasSessionWorkspace)
        {
            menu.Items.Add(CreateRunOnSeparator());
            var remove = new MenuItem
            {
                Header = CreateRunOnRowHeader(
                    iconKind: PackIconKind.LinkOff,
                    text: _loc["Context_RemoveWorkspace"],
                    trailing: RunOnTrailing.None),
                Style = itemStyle
            };
            remove.Click += (_, _) => ScheduleUi(RemoveSessionWorkspaceAsync);
            menu.Items.Add(remove);
        }

        return menu;
    }

    private static Separator CreateRunOnSeparator()
    {
        var style = Application.Current?.TryFindResource("RunOnMenuSeparatorStyle") as Style;
        return new Separator { Style = style };
    }

    private async Task DeleteSshConnectionAsync(WorkspaceSettings workspace)
    {
        var label = FormatRemoteLabel(workspace);
        if (!_notifier.ConfirmYesNo("Shell_SshDeleteConnectionTitle", "Shell_SshDeleteConnectionConfirm", label))
        {
            return;
        }

        var isActive = HasSessionWorkspace
            && _workspaceContext.Kind == WorkspaceKind.Ssh
            && !string.IsNullOrWhiteSpace(_session.ActiveWorkspaceId)
            && string.Equals(workspace.Id, _session.ActiveWorkspaceId, StringComparison.OrdinalIgnoreCase);

        if (isActive)
        {
            if (!await FileEditor.TryCloseAllTabsAsync().ConfigureAwait(true))
            {
                return;
            }

            await _sshConnection.DisconnectSessionAsync(_session.Id).ConfigureAwait(true);
            _session = _session.WithWorkspace(null, workspaceId: null);
            await ApplySessionWorkspaceAsync().ConfigureAwait(true);
            await SaveCurrentSessionIfNeededAsync().ConfigureAwait(true);
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
        ShowShellToast(_loc.Format("Shell_SshConnectionDeleted", label), ShellToastKind.Success);
        OnPropertyChanged(nameof(RunOnDisplayName));
        OnPropertyChanged(nameof(HasSessionWorkspace));
        OnPropertyChanged(nameof(WorkspacePanelActionLabel));
    }

    private enum RunOnTrailing
    {
        None,
        Check,
        Chevron
    }

    private static UIElement CreateRunOnSectionHeader(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindBrush("Brush.SubtleText") ?? Brushes.Gray,
            Margin = new Thickness(4, 2, 4, 4)
        };

    private static UIElement CreateRunOnEmptyHint(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = TryFindBrush("Brush.SubtleText") ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 10, 8, 10),
            TextWrapping = TextWrapping.Wrap
        };

    private UIElement CreateSshConnectionRowHeader(WorkspaceSettings workspace, bool selected)
    {
        var grid = new Grid { MinWidth = 210 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = AppPackIconHelper.Create(
            PackIconKind.ServerNetwork,
            size: 14,
            foreground: TryFindBrush("Brush.Text") ?? Brushes.Black,
            opacity: 0.88);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = FormatRemoteLabel(workspace),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (selected)
        {
            var check = AppPackIconHelper.Create(
                PackIconKind.Check,
                size: 12,
                foreground: TryFindBrush("Brush.SubtleText") ?? Brushes.Gray,
                margin: new Thickness(4, 0, 6, 0),
                opacity: 0.95);
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        var deleteButton = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            ToolTip = _loc["Shell_SshDeleteConnection"],
            VerticalAlignment = VerticalAlignment.Center,
            Content = AppPackIconHelper.Create(
                PackIconKind.DeleteOutline,
                size: 12,
                foreground: TryFindBrush("Brush.SubtleText") ?? Brushes.Gray,
                margin: new Thickness(0),
                opacity: 0.7)
        };
        deleteButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ScheduleUi(() => DeleteSshConnectionAsync(workspace));
        };
        Grid.SetColumn(deleteButton, 3);
        grid.Children.Add(deleteButton);

        return grid;
    }

    private static UIElement CreateRunOnRowHeader(PackIconKind iconKind, string text, RunOnTrailing trailing)
    {
        var grid = new Grid { MinWidth = 188 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = AppPackIconHelper.Create(
            iconKind,
            size: 14,
            foreground: TryFindBrush("Brush.Text") ?? Brushes.Black,
            opacity: 0.88);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (trailing != RunOnTrailing.None)
        {
            var trailingIcon = AppPackIconHelper.Create(
                trailing == RunOnTrailing.Check ? PackIconKind.Check : PackIconKind.ChevronRight,
                size: trailing == RunOnTrailing.Check ? 12 : 10,
                foreground: TryFindBrush("Brush.SubtleText") ?? Brushes.Gray,
                margin: new Thickness(12, 0, 0, 0),
                opacity: trailing == RunOnTrailing.Check ? 0.95 : 0.55);
            Grid.SetColumn(trailingIcon, 2);
            grid.Children.Add(trailingIcon);
        }

        return grid;
    }

    private static Brush? TryFindBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    private static void ScheduleUi(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _ = action();
            return;
        }

        // Defer until after ContextMenu closes so modal dialogs can take focus.
        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Surface unexpected UI failures instead of silent no-op.
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private static string FormatRemoteLabel(WorkspaceSettings workspace)
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

    private string ResolveActiveWorkspaceName()
    {
        if (!HasSessionWorkspace)
        {
            return _loc["Shell_NoWorkspace"];
        }

        if (_workspaceContext.Kind == WorkspaceKind.Ssh)
        {
            var match = WorkspaceSessionResolver.FindMatch(_session, _appSettings);
            if (match is not null)
            {
                return FormatRemoteLabel(match);
            }
        }

        return _workspaceContext.DisplayName ?? _loc["Shell_NoWorkspace"];
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
                _session.WithWorkspace(configured.RootPath, configured.Id),
                _appSettings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notifier.WarningText("Common_Prompt", _loc.Format("Shell_SshTestFailed", ex.Message));
            return;
        }

        _session = _session.WithWorkspace(configured.RootPath, configured.Id);
        await ApplySessionWorkspaceAsync().ConfigureAwait(true);
        await SaveCurrentSessionIfNeededAsync().ConfigureAwait(true);
        ShowShellToast(_loc.Format("Shell_SshConnectedStatus", FormatRemoteLabel(configured)), ShellToastKind.Success);
    }

    [RelayCommand]
    private async Task ConfigureWorkspaceAsync() =>
        await ConfigureLocalWorkspaceAsync().ConfigureAwait(true);

    private async Task ConfigureLocalWorkspaceAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = _loc["Shell_SelectWorkspace"],
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_session.ActiveWorkspace)
            && _workspaceContext.Kind == WorkspaceKind.Local
            && Directory.Exists(_session.ActiveWorkspace))
        {
            dialog.InitialDirectory = _session.ActiveWorkspace;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _sshConnection.DisconnectSessionAsync(_session.Id).ConfigureAwait(true);

        var folderName = new DirectoryInfo(dialog.FolderName).Name;
        _session = _session.WithWorkspace(dialog.FolderName, workspaceId: null);
        await ApplySessionWorkspaceAsync().ConfigureAwait(true);
        await SaveCurrentSessionIfNeededAsync().ConfigureAwait(true);
        ShowShellToast(_loc.Format("Shell_WorkspaceStatus", folderName), ShellToastKind.Success);
    }
}
