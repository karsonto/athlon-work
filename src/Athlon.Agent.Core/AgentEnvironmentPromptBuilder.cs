using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Core;

[Obsolete("Use ISystemPromptOrchestrator instead.")]
public sealed class AgentEnvironmentPromptBuilder(
    AppSettings settings,
    IAgentHostEnvironment host,
    IPlanNotebook planNotebook,
    IEnumerable<IEnvironmentPromptSection> sections) : IAgentEnvironmentPromptBuilder
{
    private readonly SystemPromptOrchestrator _orchestrator = new(
        settings,
        host,
        planNotebook,
        sections,
        Array.Empty<IPreReasoningPromptContributor>());

    public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
        _orchestrator.PrepareForTurn(session, tools).Text;
}
