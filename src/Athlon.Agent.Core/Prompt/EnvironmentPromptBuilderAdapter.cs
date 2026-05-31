namespace Athlon.Agent.Core.Prompt;

public sealed class EnvironmentPromptBuilderAdapter(ISystemPromptOrchestrator orchestrator) : IAgentEnvironmentPromptBuilder
{
    public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
        orchestrator.PrepareForTurn(session, tools).Text;
}
