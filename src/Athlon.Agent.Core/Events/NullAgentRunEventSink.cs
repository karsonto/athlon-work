using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core.Events;

public sealed class NullAgentRunEventSink : IAgentRunEventSink
{
    public static NullAgentRunEventSink Instance { get; } = new();

    public ValueTask PublishStreamEventAsync(AgentStreamEvent streamEvent, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask PublishLifecycleEventAsync(AgentRunLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
