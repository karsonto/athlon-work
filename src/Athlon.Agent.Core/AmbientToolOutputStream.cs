using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core;

/// <summary>
/// AsyncLocal-scoped stream that tools (e.g. ExecuteCommandTool) use to push
/// incremental stdout/stderr output to the UI while the tool is still running.
/// Entered by ToolInvocationPipeline before invoking the tool.
/// </summary>
public sealed class AmbientToolOutputStream : IDisposable
{
    private static readonly AsyncLocal<AmbientToolOutputStream?> Current = new();

    private readonly AgentTurnCallbacks? _callbacks;
    private readonly string _toolCallId;
    private readonly AmbientToolOutputStream? _previous;

    public AmbientToolOutputStream(AgentTurnCallbacks? callbacks, string toolCallId)
    {
        _callbacks = callbacks;
        _toolCallId = toolCallId;
        _previous = Current.Value;
        Current.Value = this;
    }

    public static AmbientToolOutputStream? CurrentStream => Current.Value;

    /// <summary>Push one line of output to the UI via the stream event system.</summary>
    public void WriteLine(string line)
    {
        if (_callbacks.OnStreamEvent is not { } onEvent)
            return;

        var evt = new AgentStreamEvent.ToolCallOutput(_toolCallId, line + Environment.NewLine);
        _ = onEvent(evt);
    }

    public void Dispose()
    {
        Current.Value = _previous;
    }
}
