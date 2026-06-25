using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Core;

public sealed record AgentSendOptions
{
    public bool? RequireToolApproval { get; init; }

    public AgentLoopOptions? LoopOptions { get; init; }
}
