using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.Terminal;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using EasyWindowsTerminalControl;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App.Controls;

public partial class TerminalWorkspaceView : UserControl
{
    private TerminalWorkspaceTabViewModel? _tab;
    private EasyTerminalControl? _control;
    private bool _attaching;
    private TerminalSessionRegistry? _registry;

    public TerminalWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
        _registry ??= TryResolveRegistry();
        AttachTerminal();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        DetachTerminal(preserveSession: true);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachTerminal(preserveSession: true);
        _tab = e.NewValue as TerminalWorkspaceTabViewModel;
        if (IsLoaded)
        {
            AttachTerminal();
        }
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        if (_control is null)
        {
            return;
        }

        try
        {
            _control.Theme = WorkspaceTerminalBootstrap.CreateTheme();
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Terminal theme update failed: {ex.Message}");
        }
    }

    private async void RestartButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_control is null || _tab is null || _tab.IsDisposed)
        {
            DetachTerminal(preserveSession: false);
            AttachTerminal();
            return;
        }

        try
        {
            ShowError(null);
            _tab.Session = null;
            await _control.RestartTerm(disposeOld: true).ConfigureAwait(true);
            _tab.Session = _control.ConPTYTerm;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            App.StartupTrace($"Terminal restart failed: {ex.Message}");
        }
    }

    private void AttachTerminal()
    {
        if (_attaching || _tab is null || _tab.IsDisposed || _control is not null)
        {
            return;
        }

        _attaching = true;
        try
        {
            EnsureShellConfigured(_tab);
            ShellLabel.Text = BuildShellCaption(_tab);
            SyncTabTitle(_tab);

            var control = WorkspaceTerminalBootstrap.CreateControl(
                _tab.StartupCommandLine,
                _tab.WorkingDirectory);

            if (_tab.Session is { } existing)
            {
                control.ConPTYTerm = existing;
            }

            TerminalHost.Children.Clear();
            TerminalHost.Children.Add(control);
            _control = control;
            _tab.Session = control.ConPTYTerm;
            RegisterSession(_tab, control.ConPTYTerm);
            ShowError(null);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            App.StartupTrace($"Terminal init failed: {ex.Message}");
        }
        finally
        {
            _attaching = false;
        }
    }

    private void DetachTerminal(bool preserveSession)
    {
        if (_control is null)
        {
            return;
        }

        TermPTY? session = null;
        try
        {
            session = _control.DisconnectConPTYTerm();
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Terminal disconnect failed: {ex.Message}");
        }

        TerminalHost.Children.Clear();
        _control = null;

        if (_tab is not null)
        {
            UnregisterSession(_tab);
        }

        if (_tab is null || _tab.IsDisposed || !preserveSession)
        {
            WorkspaceTerminalBootstrap.DisposeSession(session);
            if (_tab is not null)
            {
                _tab.Session = null;
            }

            return;
        }

        _tab.Session = session;
    }

    private static void EnsureShellConfigured(TerminalWorkspaceTabViewModel tab)
    {
        if (!string.IsNullOrWhiteSpace(tab.StartupCommandLine))
        {
            return;
        }

        var services = (Application.Current as App)?.Services;
        var preferredShell = services?.GetService<AppSettings>()?.Ui.TerminalShell;
        tab.StartupCommandLine = WorkspaceTerminalBootstrap.ResolveStartupCommandLine(preferredShell);
        if (string.IsNullOrWhiteSpace(tab.WorkingDirectory))
        {
            var workspace = services?.GetService<IActiveWorkspaceContext>();
            tab.WorkingDirectory = WorkspaceTerminalBootstrap.ResolveWorkingDirectory(workspace);
        }
    }

    private static string BuildShellCaption(TerminalWorkspaceTabViewModel tab)
    {
        var shell = string.IsNullOrWhiteSpace(tab.StartupCommandLine)
            ? "shell"
            : System.IO.Path.GetFileName(tab.StartupCommandLine);
        if (string.IsNullOrWhiteSpace(tab.WorkingDirectory))
        {
            return shell;
        }

        return $"{shell} · {tab.WorkingDirectory}";
    }

    private static void SyncTabTitle(TerminalWorkspaceTabViewModel tab)
    {
        if (string.IsNullOrWhiteSpace(tab.StartupCommandLine))
        {
            return;
        }

        var shell = System.IO.Path.GetFileNameWithoutExtension(tab.StartupCommandLine);
        if (string.IsNullOrWhiteSpace(shell))
        {
            return;
        }

        // Keep serial suffix from the initial localized title when present (e.g. "终端 4" → "pwsh 4").
        var serial = ExtractTrailingSerial(tab.Title);
        tab.Title = serial is null ? shell : $"{shell} {serial}";
    }

    private static string? ExtractTrailingSerial(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var parts = title.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var last = parts[^1];
        return int.TryParse(last, out _) ? last : null;
    }

    private void ShowError(string? detail)
    {
        var hasError = !string.IsNullOrWhiteSpace(detail);
        ErrorPanel.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        TerminalHost.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
        ErrorDetail.Text = detail ?? string.Empty;
    }

    private void RegisterSession(TerminalWorkspaceTabViewModel tab, TermPTY session)
    {
        tab.OutputBuffer.Attach(session);
        _registry ??= TryResolveRegistry();
        _registry?.Register(tab.Id, session, tab.OutputBuffer);
    }

    private void UnregisterSession(TerminalWorkspaceTabViewModel tab)
    {
        tab.OutputBuffer.Detach();
        _registry?.Unregister(tab.Id);
    }

    private static TerminalSessionRegistry? TryResolveRegistry()
    {
        try
        {
            if (Application.Current is App app)
            {
                return app.Services?.GetService<TerminalSessionRegistry>();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
