using System.IO;
using System.Windows.Media;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace Athlon.Agent.App.Services;

/// <summary>Resolves shell, cwd, and theme for right-pane interactive terminals.</summary>
internal static class WorkspaceTerminalBootstrap
{
    public const string ShellCmd = "cmd";
    public const string ShellPowerShell = "powershell";
    public const string ShellPwsh = "pwsh";

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

    public static EasyTerminalControl CreateControl(string startupCommandLine, string? workingDirectory)
    {
        var control = new EasyTerminalControl
        {
            StartupCommandLine = startupCommandLine,
            // Match Codex / Windows Terminal density (~12pt Consolas), not the larger IDE editor sizes.
            FontSizeWhenSettingTheme = 12,
            FontFamilyWhenSettingTheme = new FontFamily("Consolas, Cascadia Mono, Cascadia Code"),
            Win32InputMode = true,
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey
                | EasyTerminalControl.INPUT_CAPTURE.DirectionKeys
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
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
