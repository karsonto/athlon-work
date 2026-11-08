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

/// <summary>Builds the "run on" context menu (local vs. SSH workspace routing).</summary>
internal sealed class RunOnMenuBuilder
{
    private readonly RunOnMenuContext _ctx;

    internal RunOnMenuBuilder(RunOnMenuContext context)
    {
        _ctx = context;
    }

    internal sealed record RunOnMenuContext(
        ILocalizationService Localization,
        AppSettings AppSettings,
        IActiveWorkspaceContext WorkspaceContext,
        Func<AgentSession> GetSession,
        Func<bool> HasSessionWorkspace,
        Action<Func<Task>> ScheduleUi,
        Func<Task> ConfigureLocalWorkspaceAsync,
        Func<WorkspaceSettings, Task> ApplyConfiguredSshWorkspaceAsync,
        Func<Task> ConfigureSshWorkspaceAsync,
        Func<Task> RemoveSessionWorkspaceAsync,
        Func<WorkspaceSettings, Task> DeleteSshConnectionAsync);

    private enum RunOnTrailing
    {
        None,
        Check,
        Chevron
    }

    internal ContextMenu Build()
    {
        var ctx = _ctx;

        var menu = new ContextMenu
        {
            Style = Application.Current.TryFindResource("RunOnContextMenuStyle") as Style
        };

        var itemStyle = Application.Current.TryFindResource("RunOnMenuItemStyle") as Style;
        var isLocalActive = ctx.HasSessionWorkspace() && ctx.WorkspaceContext.Kind == WorkspaceKind.Local;
        var isSshActive = ctx.HasSessionWorkspace() && ctx.WorkspaceContext.Kind == WorkspaceKind.Ssh;
        var session = ctx.GetSession();
        var activeWorkspaceId = session.ActiveWorkspaceId;

        menu.Items.Add(new MenuItem
        {
            Header = CreateRunOnSectionHeader(ctx.Localization["Shell_RunOn"]),
            IsEnabled = false,
            Style = itemStyle
        });

        var thisPc = new MenuItem
        {
            Header = CreateRunOnRowHeader(
                iconKind: PackIconKind.Laptop,
                text: ctx.Localization["Shell_RunOnThisPc"],
                trailing: isLocalActive ? RunOnTrailing.Check : RunOnTrailing.None),
            Style = itemStyle
        };
        thisPc.Click += (_, _) => ctx.ScheduleUi(ctx.ConfigureLocalWorkspaceAsync);
        menu.Items.Add(thisPc);

        var remote = new MenuItem
        {
            Header = CreateRunOnRowHeader(
                iconKind: PackIconKind.Monitor,
                text: ctx.Localization["Shell_RunOnRemoteConnection"],
                trailing: RunOnTrailing.Chevron),
            Style = itemStyle
        };

        var sshWorkspaces = ctx.AppSettings.Workspaces
            .Where(item => item.WorkspaceKind == WorkspaceKind.Ssh && item.Ssh is not null)
            .ToList();

        if (sshWorkspaces.Count == 0)
        {
            remote.Items.Add(new MenuItem
            {
                Header = CreateRunOnEmptyHint(ctx.Localization["Shell_RunOnNoRemotes"]),
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
                        ctx.ScheduleUi(() => ctx.ApplyConfiguredSshWorkspaceAsync(configured));
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
                text: ctx.Localization["Shell_RunOnConnectSshAction"],
                trailing: RunOnTrailing.None),
            Style = itemStyle
        };
        sshItem.Click += (_, _) => ctx.ScheduleUi(ctx.ConfigureSshWorkspaceAsync);
        remote.Items.Add(sshItem);
        menu.Items.Add(remote);

        if (ctx.HasSessionWorkspace())
        {
            menu.Items.Add(CreateRunOnSeparator());
            var remove = new MenuItem
            {
                Header = CreateRunOnRowHeader(
                    iconKind: PackIconKind.LinkOff,
                    text: ctx.Localization["Context_RemoveWorkspace"],
                    trailing: RunOnTrailing.None),
                Style = itemStyle
            };
            remove.Click += (_, _) => ctx.ScheduleUi(ctx.RemoveSessionWorkspaceAsync);
            menu.Items.Add(remove);
        }

        return menu;
    }

    private static Separator CreateRunOnSeparator()
    {
        var style = Application.Current?.TryFindResource("RunOnMenuSeparatorStyle") as Style;
        return new Separator { Style = style };
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
        var ctx = _ctx;

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
            Text = SshWorkspaceCoordinator.FormatRemoteLabel(workspace),
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
            ToolTip = ctx.Localization["Shell_SshDeleteConnection"],
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
            ctx.ScheduleUi(() => ctx.DeleteSshConnectionAsync(workspace));
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
}
