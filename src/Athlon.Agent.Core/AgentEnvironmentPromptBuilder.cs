using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Core;

[Obsolete("Use ISystemPromptOrchestrator instead.")]
public sealed class AgentEnvironmentPromptBuilder(
    AppSettings settings,
    IAgentHostEnvironment host,
    IEnumerable<IEnvironmentPromptSection> sections) : IAgentEnvironmentPromptBuilder
{
    private readonly SystemPromptOrchestrator _orchestrator = new(
        settings,
        host,
        sections,
        Array.Empty<IPreReasoningPromptContributor>());

    public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
        _orchestrator.PrepareForTurn(session, tools).Text;
}
