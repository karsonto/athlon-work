using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Core.Middleware;

public sealed class AgentTurnInvocation
{
    public required AgentRunContext RunContext { get; init; }

    public required AgentSession Session { get; set; }

    public AgentTurnCallbacks? Callbacks { get; init; }

    public required AgentStreamAdapter StreamAdapter { get; init; }

    public ToolStormBreaker? ToolStorm { get; set; }

    public string? EnvironmentPrompt { get; set; }

    public string? RuntimeContext { get; set; }

    public IReadOnlyList<ToolDefinition>? Tools { get; set; }

    public FrozenSystemPrompt? FrozenPrompt { get; set; }

    public ModelMessageCache? ModelMessageCache { get; set; }

    public CompactionRuntimeContext? CompactionContext { get; set; }

    public AgentRunState State { get; } = new();
}
