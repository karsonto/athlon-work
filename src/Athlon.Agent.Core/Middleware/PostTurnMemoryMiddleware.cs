using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Core.Middleware;

public sealed class PostTurnMemoryMiddleware(
    ISessionHarnessState harnessState,
    IAgentRunContextAccessor runContextAccessor,
    IPostTurnMemoryProcessor memoryProcessor,
    IAppLogger logger) : AgentTurnMiddlewareBase
{
    private readonly IAppLogger _logger = logger.ForContext("PostTurnMemoryMiddleware");

    public override ValueTask OnTurnCompletedAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken)
    {
        if (!harnessState.IsCodingModeForActiveRun(runContextAccessor))
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
