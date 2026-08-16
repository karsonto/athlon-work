using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Core.Tools;

/// <summary>Per-turn snapshot used to decide which local tools are advertised/invokable.</summary>
public sealed record ToolAvailabilityContext(
    bool ComputerUseActive,
    bool HasWorkspace,
    WorkspaceKind WorkspaceKind,
    SessionAgentMode Mode,
    bool BrowserTabOpen,
    bool TerminalTabOpen,
    bool KnowledgeEnabled);
