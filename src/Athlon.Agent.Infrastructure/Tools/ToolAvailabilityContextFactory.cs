using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Infrastructure.Tools;

/// <summary>Assembles <see cref="ToolAvailabilityContext"/> from session/workspace collaborators.</summary>
public sealed class ToolAvailabilityContextFactory(
    IActiveAgentSessionContext activeSessionContext,
    ISessionKnowledgeState sessionKnowledgeState,
    ISessionHarnessState sessionHarnessState,
    IAgentRunContextAccessor runContextAccessor,
    IDebugPhaseAccessor debugPhaseAccessor,
    IPlanPhaseAccessor? planPhaseAccessor,
    WorkspaceGuard workspaceGuard,
    IBrowserWorkspaceState browserWorkspaceState,
    ITerminalWorkspaceState terminalWorkspaceState) : IToolAvailabilityContextFactory
{
    public ToolAvailabilityContext Create()
    {
        var sessionId = activeSessionContext.SessionId;
        var mode = ResolveSessionAgentMode();
        var debugPhase = mode == SessionAgentMode.Debug
            ? debugPhaseAccessor.GetPhase(sessionId)
            : null;
        var planPhase = mode == SessionAgentMode.Plan
            ? planPhaseAccessor?.GetPhase(sessionId)
            : null;
        return new ToolAvailabilityContext(
            ComputerUseActive: runContextAccessor.Current?.ComputerUseActive == true,
            HasWorkspace: workspaceGuard.HasConfiguredWorkspace,
            WorkspaceKind: workspaceGuard.CurrentKind,
            Mode: mode,
            BrowserTabOpen: browserWorkspaceState.HasOpenBrowserTab,
            TerminalTabOpen: terminalWorkspaceState.HasOpenTerminalTab,
            KnowledgeEnabled: sessionKnowledgeState.ShouldExposeKnowledgeTool(sessionId),
            ActiveDebugPhase: debugPhase,
            ActivePlanPhase: planPhase);
    }

    public string ComputeLocalStamp()
    {
        var ctx = Create();
        var sessionId = activeSessionContext.SessionId ?? string.Empty;
        return string.Join(
            '|',
            (int)ctx.WorkspaceKind,
            ctx.HasWorkspace,
            sessionId,
            ctx.KnowledgeEnabled,
            ctx.Mode,
            ctx.ActivePlanPhase?.ToString() ?? "off",
            ctx.ActiveDebugPhase?.ToString() ?? "off",
            ctx.BrowserTabOpen,
            ctx.TerminalTabOpen,
            ctx.ComputerUseActive);
    }

    private SessionAgentMode ResolveSessionAgentMode()
    {
        if (sessionHarnessState.IsCodingModeForActiveRun(runContextAccessor))
        {
            return SessionAgentMode.Agent;
        }

        if (sessionHarnessState.IsAskModeForActiveRun(runContextAccessor))
        {
            return SessionAgentMode.Ask;
        }

        if (sessionHarnessState.IsPlanModeForActiveRun(runContextAccessor))
        {
            return SessionAgentMode.Plan;
        }

        if (sessionHarnessState.IsDebugModeForActiveRun(runContextAccessor))
        {
            return SessionAgentMode.Debug;
        }

        return SessionAgentMode.Agent;
    }
}
