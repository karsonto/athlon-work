using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Core.Middleware;

public sealed class PostTurnMemoryMiddleware(
    IPostTurnMemoryProcessor memoryProcessor,
    IAppLogger logger) : AgentTurnMiddlewareBase
{
    private readonly IAppLogger _logger = logger.ForContext("PostTurnMemoryMiddleware");

    public override ValueTask OnTurnCompletedAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken)
    {
        // Memory is scoped per project session; flush in Agent/Coding/Ask whenever a workspace is bound.
        if (string.IsNullOrWhiteSpace(invocation.Session.ActiveWorkspace)
            && string.IsNullOrWhiteSpace(invocation.Session.ActiveWorkspaceId))
        {
            return ValueTask.CompletedTask;
        }

        var captured = new MemoryTurnContext(
            invocation.Session.Messages,
            invocation.EnvironmentPrompt,
            invocation.Tools);
        var sessionId = invocation.Session.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await memoryProcessor.ProcessAsync(captured, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Post-turn memory flush cancelled for session {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                _logger.Warning("Post-turn memory flush failed: {Error}", ex.Message);
            }
        }, cancellationToken);

        return ValueTask.CompletedTask;
    }
}
