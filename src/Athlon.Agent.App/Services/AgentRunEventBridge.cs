using Athlon.Agent.Core;
using Athlon.Agent.Core.Events;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

public sealed class AgentRunEventBridge
{
    public AgentRunEventMultiplexer Multiplexer { get; } = new();

    public AgentTurnCallbacks BuildCallbacks(SessionTurnUiController ui, LiveAgentSession? liveSession = null)
    {
        var inner = ui.BuildCallbacks(liveSession);
        Multiplexer.SubscribeStream(async (streamEvent, _) =>
        {
            if (inner.OnStreamEvent is not null)
            {
                await inner.OnStreamEvent(streamEvent).ConfigureAwait(false);
            }
        });

        return new AgentTurnCallbacks
        {
            OnSessionUpdated = inner.OnSessionUpdated,
            OnUsageRecorded = inner.OnUsageRecorded,
            OnToolApprovalRequired = inner.OnToolApprovalRequired,
            EventSink = Multiplexer
        };
    }
}
