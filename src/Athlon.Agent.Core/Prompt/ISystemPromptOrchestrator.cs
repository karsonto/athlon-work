namespace Athlon.Agent.Core.Prompt;

public interface ISystemPromptOrchestrator
{
    /// <summary>Builds the byte-stable system prefix for one user turn.</summary>
    FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools);

    /// <summary>Builds per-model-round context that is appended as an ephemeral user message.</summary>
    string? BuildRuntimeContext(AgentSession session, IReadOnlyList<ToolDefinition> tools);

    /// <summary>Returns the frozen system prefix unchanged.</summary>
    string BuildForReasoningIteration(
        FrozenSystemPrompt frozen,
        AgentSession session,
        IReadOnlyList<ToolDefinition> tools);
}
