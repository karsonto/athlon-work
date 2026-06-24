using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core.Middleware;

public sealed class ToolStormTurnMiddleware(
    AppSettings settings,
    ISessionToolStormStore sessionToolStormStore) : AgentTurnMiddlewareBase
{
    public override ValueTask OnTurnStartingAsync(AgentTurnInvocation invocation, CancellationToken cancellationToken)
    {
        invocation.ToolStorm = ResolveToolStormBreaker(invocation.Session.Id);
        invocation.State.ToolStorm = invocation.ToolStorm;
        return ValueTask.CompletedTask;
    }

    private ToolStormBreaker? ResolveToolStormBreaker(string sessionId)
    {
        var stormSettings = settings.ContextCompaction.ToolStorm;
        if (!stormSettings.Enabled)
        {
            return null;
        }

        return stormSettings.Scope == ToolStormScope.Session
            ? sessionToolStormStore.GetOrCreate(sessionId, stormSettings)
            : new ToolStormBreaker(stormSettings);
    }
}
