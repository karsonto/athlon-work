using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

/// <summary>
/// Breaks the AgentRuntime ↔ AgentTurnCoordinator cycle. Prefer <see cref="Attach"/> from the
/// AgentRuntime factory; if overflow compact runs before Attach, <see cref="_ensureRuntimeAttached"/>
/// forces IAgentRuntime resolution so Attach completes first.
/// </summary>
public sealed class AgentTurnForceCompactBridge : IAgentTurnForceCompact
{
    private readonly Func<IAgentRuntime>? _ensureRuntimeAttached;
    private IAgentTurnForceCompact? _inner;

    public AgentTurnForceCompactBridge(Func<IAgentRuntime>? ensureRuntimeAttached = null)
    {
        _ensureRuntimeAttached = ensureRuntimeAttached;
    }

    public void Attach(IAgentTurnForceCompact inner) => _inner = inner;

    public Task<(AgentSession Session, string? TokenBudgetNotice)> RunAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        PreCompletionOptions options,
        string environmentPrompt,
        string? runtimeContext,
        IReadOnlyList<ToolDefinition> tools,
        ContextPressureLevel pressure,
        CancellationToken cancellationToken)
    {
        var target = _inner;
        if (target is null && _ensureRuntimeAttached is not null)
        {
            _ = _ensureRuntimeAttached();
            target = _inner;
        }

        return (target ?? throw new InvalidOperationException("Force compact not attached."))
            .RunAsync(
                session,
                callbacks,
                options,
                environmentPrompt,
                runtimeContext,
                tools,
                pressure,
                cancellationToken);
    }
}
