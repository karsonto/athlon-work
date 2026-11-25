using Athlon.Agent.App.Services.Terminal;

namespace Athlon.Agent.App.Services;

/// <summary>How a workspace terminal tab should start.</summary>
internal sealed class TerminalLaunch
{
    public string StartupCommandLine { get; init; } = string.Empty;

    public string? WorkingDirectory { get; init; }

    public bool UseWin32Input { get; init; } = true;

    public string? Caption { get; init; }

    public bool IsRemote { get; init; }

    public string? RemoteHost { get; init; }

    public string? StartupError { get; init; }

    public SshPtyHandshakeServer? Handshake { get; init; }
}
