namespace Athlon.Agent.Core.Middleware;

public sealed class AgentTurnMiddlewarePipeline(IEnumerable<IAgentTurnMiddleware> middlewares)
{
    private readonly IAgentTurnMiddleware[] _middlewares = middlewares.ToArray();

    public async Task OnTurnStartingAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            await middleware.OnTurnStartingAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnTurnCompletedAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            await middleware.OnTurnCompletedAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnBeforeModelRoundAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            await middleware.OnBeforeModelRoundAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnAfterModelRoundAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            await middleware.OnAfterModelRoundAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnBeforeToolInvokeAsync(
        AgentTurnInvocation invocation,
        AgentToolCall toolCall,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            await middleware.OnBeforeToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnAfterToolInvokeAsync(
        AgentTurnInvocation invocation,
        AgentToolCall toolCall,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in _middlewares)
        {
            await middleware.OnAfterToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        }
    }
}
