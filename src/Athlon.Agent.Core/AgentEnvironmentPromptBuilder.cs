using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Sso;

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
        NullCurrentSsoUserContext.Instance,
        sections,
        Array.Empty<IPreReasoningPromptContributor>());

    public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
        _orchestrator.PrepareForTurn(session, tools).Text;
}
