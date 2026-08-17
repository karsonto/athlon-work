using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Core.Tools;

/// <summary>
/// Ordered local-tool enablement rules. First matching rule that returns a non-null
/// decision wins; otherwise the tool is allowed.
/// </summary>
public static class ToolAvailabilityPolicy
{
    private readonly record struct Rule(
        string Name,
        Func<ToolAvailabilityContext, bool> When,
        Func<ToolAvailabilityContext, ToolFacet, bool?> Decide);

    // Priority matches the former McpDelegatingToolRouter.IsToolEnabled if-chain.
    private static readonly Rule[] Rules =
    [
        new(
            "computer-use-exclusive",
            static ctx => ctx.ComputerUseActive,
            static (_, facets) => facets.HasFlag(ToolFacet.ComputerUse)),
        new(
            "computer-use-blocked-outside-cu",
            static ctx => !ctx.ComputerUseActive,
            static (_, facets) => facets.HasFlag(ToolFacet.ComputerUse) ? false : null),
        new(
            "chat-only",
            static ctx => !ctx.HasWorkspace,
            static (ctx, facets) =>
            {
                if (facets.HasFlag(ToolFacet.BrowserBootstrap)
                    || facets.HasFlag(ToolFacet.TerminalBootstrap))
                {
                    return true;
                }

                if (facets.HasFlag(ToolFacet.Browser) && ctx.BrowserTabOpen)
                {
                    return true;
                }

                if (facets.HasFlag(ToolFacet.Terminal) && ctx.TerminalTabOpen)
                {
                    return true;
                }

                if (facets.HasFlag(ToolFacet.Knowledge) && ctx.KnowledgeEnabled)
                {
                    return true;
                }

                return false;
            }),
        new(
            "local-tools-on-ssh",
            static ctx => ctx.WorkspaceKind == WorkspaceKind.Ssh,
            static (_, facets) => facets.HasFlag(ToolFacet.LocalWorkspace) ? false : null),
        new(
            "remote-tools-off-ssh",
            static ctx => ctx.WorkspaceKind != WorkspaceKind.Ssh,
            static (_, facets) => facets.HasFlag(ToolFacet.RemoteWorkspace) ? false : null),
        new(
            "harness-todo-coding-only",
            static ctx => ctx.Mode != SessionAgentMode.Coding,
            static (_, facets) => facets.HasFlag(ToolFacet.HarnessTodo) ? false : null),
        new(
            "plan-tools-plan-only",
            static ctx => ctx.Mode != SessionAgentMode.Plan,
            static (_, facets) => facets.HasFlag(ToolFacet.Plan) ? false : null),
        new(
            "browser-requires-tab",
            static ctx => !ctx.BrowserTabOpen,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.Browser) && !facets.HasFlag(ToolFacet.BrowserBootstrap)
                    ? false
                    : null),
        new(
            "terminal-requires-tab",
            static ctx => !ctx.TerminalTabOpen,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.Terminal) && !facets.HasFlag(ToolFacet.TerminalBootstrap)
                    ? false
                    : null),
        // Memory without workspace is already rejected by chat-only (never null there).
        new(
            "ask-plan-block-writes-and-subagents",
            static ctx => ctx.Mode is SessionAgentMode.Ask or SessionAgentMode.Plan,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.WriteFileOrShell)
                || facets.HasFlag(ToolFacet.Shell)
                || facets.HasFlag(ToolFacet.SubAgent)
                    ? false
                    : null),
        new(
            "debug-read-logs-analyze-only",
            static ctx => ctx.Mode == SessionAgentMode.Debug,
            static (ctx, facets) =>
                facets.HasFlag(ToolFacet.Debug)
                    ? ctx.ActiveDebugPhase == DebugPhase.Analyze
                    : null),
        new(
            "debug-mode-block-subagents-and-shell",
            static ctx => ctx.Mode == SessionAgentMode.Debug,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.SubAgent) || facets.HasFlag(ToolFacet.Shell) ? false : null),
        new(
            "debug-mode-hypothesize-readonly",
            static ctx => ctx.Mode == SessionAgentMode.Debug && ctx.ActiveDebugPhase == DebugPhase.Hypothesize,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.WriteFileOrShell) || facets.HasFlag(ToolFacet.Debug) ? false : null),
        new(
            "debug-mode-analyze-readonly",
            static ctx => ctx.Mode == SessionAgentMode.Debug && ctx.ActiveDebugPhase == DebugPhase.Analyze,
            static (_, facets) => facets.HasFlag(ToolFacet.WriteFileOrShell) ? false : null),
        new(
            "knowledge-session-gate",
            static _ => true,
            static (ctx, facets) =>
                facets.HasFlag(ToolFacet.Knowledge) ? ctx.KnowledgeEnabled : null)
    ];

    public static bool IsEnabled(IAgentTool tool, ToolAvailabilityContext context)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);

        var facets = ToolFacetClassifier.Classify(tool);
        foreach (var rule in Rules)
        {
            if (!rule.When(context))
            {
                continue;
            }

            var decision = rule.Decide(context, facets);
            if (decision is { } enabled)
            {
                return enabled;
            }
        }

        return true;
    }
}
