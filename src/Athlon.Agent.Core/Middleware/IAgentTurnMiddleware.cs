namespace Athlon.Agent.Core.Middleware;

public interface IAgentTurnMiddleware
{
    ValueTask OnTurnStartingAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken);

    ValueTask OnTurnCompletedAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken);

    ValueTask OnBeforeModelRoundAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken);

    ValueTask OnAfterModelRoundAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken);

    ValueTask OnBeforeToolInvokeAsync(
        AgentTurnInvocation invocation,
        AgentToolCall toolCall,
        CancellationToken cancellationToken);

    ValueTask OnAfterToolInvokeAsync(
        AgentTurnInvocation invocation,
        AgentToolCall toolCall,
        CancellationToken cancellationToken);
}
