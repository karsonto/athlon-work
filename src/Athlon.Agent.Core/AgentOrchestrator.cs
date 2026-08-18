namespace Athlon.Agent.Core;

public sealed class AgentOrchestrator(IAgentRuntime agentRuntime) : IAgentOrchestrator
{
    public Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default,
        bool computerUseActive = false,
        bool appendUserMessage = true) =>
        agentRuntime.SendAsync(
            session,
            userInput,
            imageAttachments,
            callbacks,
            cancellationToken,
            computerUseActive,
            appendUserMessage: appendUserMessage);
}
