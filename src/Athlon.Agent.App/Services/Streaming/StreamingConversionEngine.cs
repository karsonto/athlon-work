using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.Streaming;

public sealed class StreamingConversionEngine
{
    private int _streamSequence;

    public StreamingConversionResult Process(StreamingConversionState state, StreamingStreamEvent streamEvent)
    {
        return streamEvent switch
        {
            StreamingStreamEvent.TextDelta text => ProcessTextDelta(state, text.Content),
            StreamingStreamEvent.ReasoningDelta reasoning => ProcessReasoningDelta(state, reasoning.Content),
            StreamingStreamEvent.ToolCallDelta tool => ProcessToolCallDelta(state, tool.Delta),
            StreamingStreamEvent.ToolExecutionStarted tool => ProcessToolExecutionStarted(state, tool.ToolCall),
            StreamingStreamEvent.AssistantMessagePersisted message =>
                ProcessAssistantMessagePersisted(state, message.Message),
            StreamingStreamEvent.ClearEmptyAssistantPlaceholder =>
                ProcessClearEmptyAssistantPlaceholder(state),
            StreamingStreamEvent.TurnReset => ProcessTurnReset(state),
            StreamingStreamEvent.TurnFinalize => ProcessTurnFinalize(state),
            _ => new StreamingConversionResult(state, [])
        };
    }

    private StreamingConversionResult ProcessTextDelta(StreamingConversionState state, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new StreamingConversionResult(state, []);
        }

        var effects = new List<StreamingUiEffect>();
        var streamId = EnsureActiveTextStream(state, effects);
        effects.Add(new StreamingUiEffect.AppendAssistantText(streamId, content));
        return new StreamingConversionResult(state, effects);
    }

    private StreamingConversionResult ProcessReasoningDelta(StreamingConversionState state, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new StreamingConversionResult(state, []);
        }

        var effects = new List<StreamingUiEffect>();
        var streamId = EnsureActiveReasoningStream(state, effects);
        effects.Add(new StreamingUiEffect.AppendAssistantReasoning(streamId, content));
        return new StreamingConversionResult(state, effects);
    }

    private StreamingConversionResult ProcessToolCallDelta(
        StreamingConversionState state,
        StreamingToolCallDelta delta)
    {
        var effects = new List<StreamingUiEffect>();
        EndActiveAssistantStreamsForToolBoundary(state, effects);

        var toolCallId = string.IsNullOrWhiteSpace(delta.Id)
            ? $"stream-tool-{delta.Index}"
            : delta.Id;

        if (!state.HasStartedToolCall(toolCallId))
        {
            state.StartToolCall(toolCallId);
            effects.Add(new StreamingUiEffect.EnsureToolBubble(delta.Index, delta.Id, delta.Name));
        }

        state.ToolStreamIndexToCallId[delta.Index] = toolCallId;
        effects.Add(new StreamingUiEffect.UpdateToolBubble(
            delta.Index,
            delta.Id,
            delta.Name,
            delta.ArgumentsJson));
        effects.Add(new StreamingUiEffect.RequestScroll());
        return new StreamingConversionResult(state, effects);
    }

    private StreamingConversionResult ProcessToolExecutionStarted(
        StreamingConversionState state,
        AgentToolCall toolCall)
    {
        var effects = new List<StreamingUiEffect>();
        EndActiveAssistantStreamsForToolBoundary(state, effects);
        effects.Add(new StreamingUiEffect.PromoteToolBubbleToRunning(null, toolCall));
        effects.Add(new StreamingUiEffect.RequestScroll());
        return new StreamingConversionResult(state, effects);
    }

    private StreamingConversionResult ProcessAssistantMessagePersisted(
        StreamingConversionState state,
        ChatMessage message)
    {
        var effects = new List<StreamingUiEffect>();
        if (state.ActiveAssistantStreamId is { } streamId)
        {
            effects.Add(new StreamingUiEffect.CompleteAssistantBubble(streamId, message));
            EndAssistantStream(state, streamId, effects, removeIfEmpty: false);
            effects.Add(new StreamingUiEffect.RequestScrollImmediate());
        }

        return new StreamingConversionResult(state, effects);
    }

    private StreamingConversionResult ProcessClearEmptyAssistantPlaceholder(StreamingConversionState state)
    {
        var effects = new List<StreamingUiEffect>();
        if (state.ActiveAssistantStreamId is { } streamId)
        {
            EndAssistantStream(state, streamId, effects, removeIfEmpty: true);
        }

        return new StreamingConversionResult(state, effects);
    }

    private StreamingConversionResult ProcessTurnReset(StreamingConversionState state)
    {
        state.Reset();
        return new StreamingConversionResult(state, []);
    }

    private StreamingConversionResult ProcessTurnFinalize(StreamingConversionState state)
    {
        var effects = new List<StreamingUiEffect>();
        foreach (var streamId in state.StartedTextStreams.Union(state.StartedReasoningStreams))
        {
            var textOpen = state.HasStartedTextStream(streamId) && !state.HasEndedTextStream(streamId);
            var reasoningOpen = state.HasStartedReasoningStream(streamId) && !state.HasEndedReasoningStream(streamId);
            if (!textOpen && !reasoningOpen)
            {
                continue;
            }

            effects.Add(new StreamingUiEffect.SealAssistantBubble(streamId));
            if (textOpen)
            {
                state.EndTextStream(streamId);
            }

            if (reasoningOpen)
            {
                state.EndReasoningStream(streamId);
            }
        }

        state.ActiveAssistantStreamId = null;
        return new StreamingConversionResult(state, effects);
    }

    private string EnsureActiveTextStream(StreamingConversionState state, List<StreamingUiEffect> effects)
    {
        if (state.HasActiveTextStream())
        {
            return state.CurrentTextStreamId!;
        }

        var streamId = AllocateStreamId();
        state.StartTextStream(streamId);
        effects.Add(new StreamingUiEffect.EnsureAssistantBubble(streamId));
        return streamId;
    }

    private string EnsureActiveReasoningStream(StreamingConversionState state, List<StreamingUiEffect> effects)
    {
        if (state.HasActiveReasoningStream())
        {
            return state.CurrentReasoningStreamId!;
        }

        var streamId = state.ActiveAssistantStreamId ?? AllocateStreamId();
        if (!state.HasStartedTextStream(streamId))
        {
            state.StartTextStream(streamId);
        }

        state.StartReasoningStream(streamId);
        effects.Add(new StreamingUiEffect.EnsureAssistantBubble(streamId));
        return streamId;
    }

    private void EndActiveAssistantStreamsForToolBoundary(
        StreamingConversionState state,
        List<StreamingUiEffect> effects)
    {
        if (state.ActiveAssistantStreamId is not { } streamId)
        {
            return;
        }

        effects.Add(new StreamingUiEffect.ReleaseAssistantBubbleForToolBoundary(streamId));
        if (state.HasActiveTextStream())
        {
            state.EndTextStream(streamId);
        }

        if (state.HasActiveReasoningStream())
        {
            state.EndReasoningStream(streamId);
        }
    }

    private void EndAssistantStream(
        StreamingConversionState state,
        string streamId,
        List<StreamingUiEffect> effects,
        bool removeIfEmpty)
    {
        if (removeIfEmpty)
        {
            effects.Add(new StreamingUiEffect.RemoveEmptyAssistantBubble(streamId));
        }
        else
        {
            effects.Add(new StreamingUiEffect.SealAssistantBubble(streamId));
        }

        if (state.HasActiveTextStream() && string.Equals(state.CurrentTextStreamId, streamId, StringComparison.Ordinal))
        {
            state.EndTextStream(streamId);
        }

        if (state.HasActiveReasoningStream()
            && string.Equals(state.CurrentReasoningStreamId, streamId, StringComparison.Ordinal))
        {
            state.EndReasoningStream(streamId);
        }
    }

    private string AllocateStreamId()
    {
        _streamSequence++;
        return $"assistant-stream-{_streamSequence}";
    }
}

public sealed record StreamingConversionResult(
    StreamingConversionState State,
    IReadOnlyList<StreamingUiEffect> Effects);
