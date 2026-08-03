namespace Athlon.Agent.Core.Browser;

/// <summary>Tracks whether the workspace has at least one Browser tab.</summary>
public interface IBrowserWorkspaceState
{
    bool HasOpenBrowserTab { get; }
}
