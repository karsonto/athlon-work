using Athlon.Agent.Core.Events;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core.Events;

public interface IAgentRunEventSink
{
    ValueTask PublishStreamEventAsync(AgentStreamEvent streamEvent, CancellationToken cancellationToken = default);

    ValueTask PublishLifecycleEventAsync(AgentRunLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default);
}

public sealed class AgentRunEventMultiplexer : IAgentRunEventSink
{
    private readonly List<Func<AgentStreamEvent, CancellationToken, ValueTask>> _streamHandlers = [];
    private readonly List<Func<AgentRunLifecycleEvent, CancellationToken, ValueTask>> _lifecycleHandlers = [];

    public void SubscribeStream(Func<AgentStreamEvent, CancellationToken, ValueTask> handler) =>
        _streamHandlers.Add(handler);

    public void SubscribeLifecycle(Func<AgentRunLifecycleEvent, CancellationToken, ValueTask> handler) =>
        _lifecycleHandlers.Add(handler);

    public async ValueTask PublishStreamEventAsync(AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        foreach (var handler in _streamHandlers)
        {
            await handler(streamEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask PublishLifecycleEventAsync(AgentRunLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
    {
        foreach (var handler in _lifecycleHandlers)
        {
            await handler(lifecycleEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
