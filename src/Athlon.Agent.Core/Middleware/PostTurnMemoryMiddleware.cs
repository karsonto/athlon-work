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

        var capturedSession = invocation.Session;
        _ = Task.Run(async () =>
        {
            try
            {
                await memoryProcessor.ProcessAsync(capturedSession.Messages, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Post-turn memory flush cancelled for session {SessionId}", capturedSession.Id);
            }
            catch (Exception ex)
            {
                _logger.Warning("Post-turn memory flush failed: {Error}", ex.Message);
            }
        }, cancellationToken);

        return ValueTask.CompletedTask;
    }
}
