using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed class AgentTurnForceCompactBridge : IAgentTurnForceCompact
{
    private IAgentTurnForceCompact? _inner;

    public void Attach(IAgentTurnForceCompact inner) => _inner = inner;

    public Task<(AgentSession Session, string? TokenBudgetNotice)> RunAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        PreCompletionOptions options,
        string environmentPrompt,
        string? runtimeContext,
        IReadOnlyList<ToolDefinition> tools,
        ContextPressureLevel pressure,
        CancellationToken cancellationToken) =>
        (_inner ?? throw new InvalidOperationException("Force compact not attached."))
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
