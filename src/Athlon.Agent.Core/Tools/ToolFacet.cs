namespace Athlon.Agent.Core.Tools;

/// <summary>Capability tags used by <see cref="ToolAvailabilityPolicy"/>.</summary>
[Flags]
public enum ToolFacet
{
    None = 0,
    ComputerUse = 1 << 0,
    LocalWorkspace = 1 << 1,
    RemoteWorkspace = 1 << 2,
    Browser = 1 << 3,
    Terminal = 1 << 4,
    /// <summary><c>browser_navigate</c> — may open a tab; does not require an open tab.</summary>
    BrowserBootstrap = 1 << 5,
    /// <summary><c>terminal_open</c> — may open a tab; does not require an open tab.</summary>
    TerminalBootstrap = 1 << 6,
    HarnessTodo = 1 << 7,
    SubAgent = 1 << 9,
    Memory = 1 << 10,
    Knowledge = 1 << 11,
    /// <summary>file_write / file_edit / apply_patch / execute_command.</summary>
    WriteFileOrShell = 1 << 12,
    /// <summary>execute_command.</summary>
    Shell = 1 << 14
}
