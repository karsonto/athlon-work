namespace Athlon.Agent.Core.Terminal;

/// <summary>Tracks whether the workspace has at least one Terminal tab.</summary>
public interface ITerminalWorkspaceState
{
    bool HasOpenTerminalTab { get; }
}
