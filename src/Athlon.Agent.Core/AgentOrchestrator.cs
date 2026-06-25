namespace Athlon.Agent.Core;

public sealed class AgentOrchestrator(IAgentRuntime agentRuntime) : IAgentOrchestrator
{
    public Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        AgentSendOptions? options = null,
        CancellationToken cancellationToken = default) =>
        agentRuntime.SendAsync(session, userInput, imageAttachments, callbacks, options, cancellationToken);
}
