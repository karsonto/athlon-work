using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public interface IAgentTurnForceCompact
{
    Task<(AgentSession Session, string? TokenBudgetNotice)> RunAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        PreCompletionOptions options,
        string environmentPrompt,
        string? runtimeContext,
        IReadOnlyList<ToolDefinition> tools,
        ContextPressureLevel pressure,
        CancellationToken cancellationToken);
}
