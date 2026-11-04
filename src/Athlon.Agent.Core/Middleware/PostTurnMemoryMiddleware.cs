using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Core.Middleware;

public sealed class PostTurnMemoryMiddleware(
    IPostTurnMemoryProcessor memoryProcessor,
    AppSettings settings,
    IAppLogger logger) : AgentTurnMiddlewareBase
{
    private readonly IAppLogger _logger = logger.ForContext("PostTurnMemoryMiddleware");

    public override ValueTask OnTurnCompletedAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken)
    {
        // Session memory needs a workspace, but the account-level habit profile does not: a chat-only
        // session still teaches the agent how this person wants to be talked to.
        var workspaceBound = !string.IsNullOrWhiteSpace(invocation.Session.ActiveWorkspace)
            || !string.IsNullOrWhiteSpace(invocation.Session.ActiveWorkspaceId);
        if (!workspaceBound && !settings.Profile.Enabled)
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
