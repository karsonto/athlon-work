using System.IO;
using System.Windows.Media;
using Athlon.Agent.App.Services.Terminal;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace Athlon.Agent.App.Services;

/// <summary>Resolves shell, cwd, and theme for right-pane interactive terminals.</summary>
internal static class WorkspaceTerminalBootstrap
{
    public const string ShellCmd = "cmd";
    public const string ShellPowerShell = "powershell";
    public const string ShellPwsh = "pwsh";
    public const string BridgeFileName = "Athlon.Agent.SshPtyBridge.exe";

    // Campbell palette (COLORREF / BGR byte order), matching Windows Terminal defaults.
    private static readonly uint[] CampbellColorTable =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00,
        0x0037DA, 0x881391, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5,
        0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2
    ];

    public static string NormalizeShellPreference(string? preferredShell)
    {
        if (string.Equals(preferredShell, ShellPwsh, StringComparison.OrdinalIgnoreCase))
        {
            return ShellPwsh;
        }

        if (string.Equals(preferredShell, ShellPowerShell, StringComparison.OrdinalIgnoreCase)
            || string.Equals(preferredShell, "WindowsPowerShell", StringComparison.OrdinalIgnoreCase))
        {
            return ShellPowerShell;
        }

        return ShellCmd;
    }

    public static string ResolveStartupCommandLine(string? preferredShell = null)
    {
        var preference = NormalizeShellPreference(preferredShell);
        string[] order = preference switch
        {
            ShellPwsh => ["pwsh.exe", "powershell.exe", "cmd.exe"],
            ShellPowerShell => ["powershell.exe", "pwsh.exe", "cmd.exe"],
            _ => ["cmd.exe", "powershell.exe", "pwsh.exe"]
        };

        foreach (var candidate in order)
        {
            if (ExecutableExists(candidate))
            {
                return candidate;
            }
        }

        return "cmd.exe";
    }

    public static string? ResolveWorkingDirectory(IActiveWorkspaceContext? workspace)
    {
        if (workspace?.Kind == WorkspaceKind.Local
            && !string.IsNullOrWhiteSpace(workspace.RootPath)
            && Directory.Exists(workspace.RootPath))
        {
            return Path.GetFullPath(workspace.RootPath);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : null;
    }

    public static string BridgeExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, BridgeFileName);

    public static async Task<TerminalLaunch> PrepareAsync(
        AppSettings settings,
        IActiveWorkspaceContext workspace,
        SshWorkspaceConnectionService ssh,
        CancellationToken cancellationToken = default)
    {
        if (workspace.Kind != WorkspaceKind.Ssh)
        {
            return PrepareLocal(settings, workspace);
        }

        try
        {
            return await PrepareRemoteAsync(settings, workspace, ssh, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TerminalLaunch
            {
                IsRemote = true,
                UseWin32Input = false,
                WorkingDirectory = workspace.RootPath,
                RemoteHost = string.IsNullOrWhiteSpace(workspace.DisplayName) ? null : workspace.DisplayName,
                StartupError = ex.Message
            };
        }
    }

    public static void Apply(TerminalWorkspaceTabViewModel tab, TerminalLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(launch);
        var previous = tab.Handshake;
        tab.Handshake = launch.Handshake;
        if (!ReferenceEquals(previous, launch.Handshake))
        {
            previous?.Dispose();
        }

        tab.StartupCommandLine = launch.StartupCommandLine;
        tab.WorkingDirectory = launch.WorkingDirectory;
        tab.UseWin32Input = launch.UseWin32Input;
        tab.Caption = launch.Caption;
        tab.IsRemote = launch.IsRemote;
        tab.StartupError = launch.StartupError;
    }

    private static TerminalLaunch PrepareLocal(AppSettings settings, IActiveWorkspaceContext workspace)
    {
        var command = ResolveStartupCommandLine(settings.Ui.TerminalShell);
        var cwd = ResolveWorkingDirectory(workspace);
        return new TerminalLaunch
        {
            StartupCommandLine = command,
            WorkingDirectory = cwd,
            UseWin32Input = true,
            IsRemote = false
        };
    }

    private static async Task<TerminalLaunch> PrepareRemoteAsync(
        AppSettings settings,
        IActiveWorkspaceContext workspace,
        SshWorkspaceConnectionService ssh,
        CancellationToken cancellationToken)
    {
        var configured = FindSshWorkspace(settings, workspace)
            ?? throw new InvalidOperationException("SSH workspace settings are missing.");
        var request = await ssh.BuildRequestAsync(configured, cancellationToken).ConfigureAwait(false);
        var bridge = BridgeExecutablePath;
        if (!File.Exists(bridge))
        {
            throw new InvalidOperationException("SSH terminal bridge was not found.");
        }

        var handshake = SshPtyHandshakeServer.Start(request);
        var root = string.IsNullOrWhiteSpace(request.RemoteRoot) ? "/" : request.RemoteRoot;
        return new TerminalLaunch
        {
            StartupCommandLine = SshPtyHandshake.BuildCommandLine(bridge, handshake.PipeName),
            WorkingDirectory = root,
            UseWin32Input = false,
            Caption = $"{request.Username}@{request.Host} · {root}",
            IsRemote = true,
            RemoteHost = request.Host,
            Handshake = handshake
        };
    }

    private static WorkspaceSettings? FindSshWorkspace(AppSettings settings, IActiveWorkspaceContext workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace.WorkspaceId))
        {
            var byId = settings.Workspaces.FirstOrDefault(item =>
                string.Equals(item.Id, workspace.WorkspaceId, StringComparison.Ordinal));
            if (byId?.WorkspaceKind == WorkspaceKind.Ssh)
            {
                return byId;
            }
        }

        if (string.IsNullOrWhiteSpace(workspace.RootPath))
        {
            return null;
        }

        var root = RemotePathNormalizer.NormalizeRoot(workspace.RootPath);
        return settings.Workspaces.FirstOrDefault(item =>
            item.WorkspaceKind == WorkspaceKind.Ssh
            && string.Equals(RemotePathNormalizer.NormalizeRoot(item.RootPath), root, StringComparison.Ordinal));
    }

    public static TerminalTheme CreateTheme()
    {
        var chrome = AppThemeManager.Current.Chrome;
        return new TerminalTheme
        {
            DefaultBackground = EasyTerminalControl.ColorToVal(chrome.Panel),
            DefaultForeground = EasyTerminalControl.ColorToVal(chrome.Text),
            DefaultSelectionBackground = EasyTerminalControl.ColorToVal(chrome.Accent),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable = CampbellColorTable
        };
    }

    public static EasyTerminalControl CreateControl(
        string startupCommandLine,
        string? workingDirectory,
        bool useWin32Input = true)
    {
        var control = new EasyTerminalControl
        {
            StartupCommandLine = startupCommandLine,
            // Match Codex / Windows Terminal density (~12pt Consolas), not the larger IDE editor sizes.
            FontSizeWhenSettingTheme = 12,
            FontFamilyWhenSettingTheme = new FontFamily("Consolas, Cascadia Mono, Cascadia Code"),
            Win32InputMode = useWin32Input,
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey
                | EasyTerminalControl.INPUT_CAPTURE.DirectionKeys
        };
        // Remote cwd is a Unix path. Only hand a directory to ConPTY when it exists on this machine.
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            control.WorkingDirectory = workingDirectory;
        }

        control.Theme = CreateTheme();
        return control;
    }

    public static void DisposeSession(TermPTY? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            session.CloseStdinToApp();
        }
        catch
        {
            // Best-effort teardown.
        }

        try
        {
            session.StopExternalTermOnly();
        }
        catch
        {
            // Best-effort teardown.
        }
    }

    private static bool ExecutableExists(string fileName)
    {
        try
        {
            var system = Path.Combine(Environment.SystemDirectory, fileName);
            if (File.Exists(system))
            {
                return true;
            }
        }
        catch
        {
            // Fall through to PATH search.
        }

        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv))
            {
                return false;
            }

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(full))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
