using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Core.Tools;

/// <summary>
/// Ordered local-tool enablement rules. First matching rule that returns a non-null
/// decision wins; otherwise the tool is allowed.
/// </summary>
public static class ToolAvailabilityPolicy
{
    private sealed record Rule(
        string Name,
        Func<ToolAvailabilityContext, bool> When,
        Func<ToolAvailabilityContext, ToolFacet, bool?> Decide) : IToolAvailabilityRule
    {
        bool IToolAvailabilityRule.Applies(ToolAvailabilityContext context) => When(context);

        bool? IToolAvailabilityRule.Decide(ToolAvailabilityContext context, ToolFacet facets) =>
            Decide(context, facets);

        string IToolAvailabilityRule.Name => Name;
    }

    /// <summary>Default rule chain (priority matches the historical IsToolEnabled if-chain).</summary>
    public static IReadOnlyList<IToolAvailabilityRule> DefaultRules { get; } =
    [
        new Rule(
            "computer-use-exclusive",
            static ctx => ctx.ComputerUseActive,
            static (_, facets) => facets.HasFlag(ToolFacet.ComputerUse)),
        new Rule(
            "computer-use-blocked-outside-cu",
            static ctx => !ctx.ComputerUseActive,
            static (_, facets) => facets.HasFlag(ToolFacet.ComputerUse) ? false : null),
        new Rule(
            "chat-only",
            static ctx => !ctx.HasWorkspace,
            static (ctx, facets) =>
            {
                if (ctx.Mode == SessionAgentMode.Ask
                    && (facets.HasFlag(ToolFacet.Terminal)
                        || facets.HasFlag(ToolFacet.TerminalBootstrap)
                        || facets.HasFlag(ToolFacet.Shell)
                        || facets.HasFlag(ToolFacet.WriteFileOrShell)))
                {
                    return false;
                }

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
        new Rule(
            "local-tools-on-ssh",
            static ctx => ctx.WorkspaceKind == WorkspaceKind.Ssh,
            static (_, facets) => facets.HasFlag(ToolFacet.LocalWorkspace) ? false : null),
        new Rule(
            "remote-tools-off-ssh",
            static ctx => ctx.WorkspaceKind != WorkspaceKind.Ssh,
            static (_, facets) => facets.HasFlag(ToolFacet.RemoteWorkspace) ? false : null),
        new Rule(
            "harness-todo-ask-plan-only",
            static ctx => ctx.Mode == SessionAgentMode.Ask || ctx.Mode == SessionAgentMode.Plan,
            static (_, facets) => facets.HasFlag(ToolFacet.HarnessTodo) ? false : null),
        new Rule(
            "plan-document-plan-mode-only",
            static ctx => ctx.Mode != SessionAgentMode.Plan,
            static (_, facets) => facets.HasFlag(ToolFacet.PlanDocument) ? false : null),
        new Rule(
            "plan-block-writes-shell-subagents",
            static ctx => ctx.Mode == SessionAgentMode.Plan,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.WriteFileOrShell)
                || facets.HasFlag(ToolFacet.Shell)
                || facets.HasFlag(ToolFacet.SubAgent)
                    ? false
                    : null),
        new Rule(
            "browser-requires-tab",
            static ctx => !ctx.BrowserTabOpen,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.Browser) && !facets.HasFlag(ToolFacet.BrowserBootstrap)
                    ? false
                    : null),
        new Rule(
            "terminal-requires-tab",
            static ctx => !ctx.TerminalTabOpen,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.Terminal) && !facets.HasFlag(ToolFacet.TerminalBootstrap)
                    ? false
                    : null),
        new Rule(
            "ask-block-writes-shell-terminal-subagents",
            static ctx => ctx.Mode == SessionAgentMode.Ask,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.WriteFileOrShell)
                || facets.HasFlag(ToolFacet.Shell)
                || facets.HasFlag(ToolFacet.Terminal)
                || facets.HasFlag(ToolFacet.TerminalBootstrap)
                || facets.HasFlag(ToolFacet.SubAgent)
                    ? false
                    : null),
        new Rule(
            "debug-mode-block-subagents-and-shell",
            static ctx => ctx.Mode == SessionAgentMode.Debug,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.SubAgent) || facets.HasFlag(ToolFacet.Shell) ? false : null),
        new Rule(
            "debug-mode-hypothesize-readonly",
            static ctx => ctx.Mode == SessionAgentMode.Debug && ctx.ActiveDebugPhase == DebugPhase.Hypothesize,
            static (_, facets) =>
                facets.HasFlag(ToolFacet.WriteFileOrShell) ? false : null),
        new Rule(
            "debug-mode-analyze-readonly",
            static ctx => ctx.Mode == SessionAgentMode.Debug && ctx.ActiveDebugPhase == DebugPhase.Analyze,
            static (_, facets) => facets.HasFlag(ToolFacet.WriteFileOrShell) ? false : null),
        new Rule(
            "debug-mode-await-readonly",
            static ctx => ctx.Mode == SessionAgentMode.Debug && ctx.ActiveDebugPhase is { } phase && phase.IsReadOnlyFollowUp(),
            static (_, facets) =>
                facets.HasFlag(ToolFacet.WriteFileOrShell)
                || facets.HasFlag(ToolFacet.Shell)
                || facets.HasFlag(ToolFacet.SubAgent)
                    ? false
                    : null),
        new Rule(
            "knowledge-session-gate",
            static _ => true,
            static (ctx, facets) =>
                facets.HasFlag(ToolFacet.Knowledge) ? ctx.KnowledgeEnabled : null)
    ];

    public static bool IsEnabled(
        IAgentTool tool,
        ToolAvailabilityContext context,
        IEnumerable<IToolAvailabilityRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);

        var facets = ToolFacetClassifier.Classify(tool);
        foreach (var rule in rules ?? DefaultRules)
        {
            if (!rule.Applies(context))
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
