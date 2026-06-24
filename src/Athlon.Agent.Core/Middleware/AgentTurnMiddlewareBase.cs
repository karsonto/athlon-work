namespace Athlon.Agent.Core.Middleware;

public abstract class AgentTurnMiddlewareBase : IAgentTurnMiddleware
{
    public virtual ValueTask OnTurnStartingAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnTurnCompletedAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnBeforeModelRoundAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnAfterModelRoundAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnBeforeToolInvokeAsync(
        AgentTurnInvocation invocation,
        AgentToolCall toolCall,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnAfterToolInvokeAsync(
        AgentTurnInvocation invocation,
        AgentToolCall toolCall,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
