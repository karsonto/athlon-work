namespace Athlon.Agent.Core.Prompt;

public interface ISystemPromptOrchestrator
{
    FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools);

    string BuildForReasoningIteration(
        FrozenSystemPrompt frozen,
        AgentSession session,
        IReadOnlyList<ToolDefinition> tools);
}
