using Athlon.Agent.App.Services.Streaming;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class StreamingConversionEngineTests
{
    private readonly StreamingConversionEngine _engine = new();

    [Fact]
    public void TextDelta_opens_single_stream_and_appends()
    {
        var state = new StreamingConversionState();

        var first = _engine.Process(state, new StreamingStreamEvent.TextDelta("hello "));
        var second = _engine.Process(first.State, new StreamingStreamEvent.TextDelta("world"));

        Assert.Single(first.Effects.OfType<StreamingUiEffect.EnsureAssistantBubble>());
        Assert.Equal(2, second.Effects.OfType<StreamingUiEffect.AppendAssistantText>().Count());
        Assert.True(second.State.HasActiveTextStream());
    }

    [Fact]
    public void ToolCallDelta_after_text_seals_stream_and_starts_tool()
    {
        var state = new StreamingConversionState();
        state = _engine.Process(state, new StreamingStreamEvent.TextDelta("before")).State;

        var result = _engine.Process(state, new StreamingStreamEvent.ToolCallDelta(
            new StreamingToolCallDelta(0, "call-1", "read_file", "{}")));

        Assert.Contains(
            result.Effects,
            effect => effect is StreamingUiEffect.ReleaseAssistantBubbleForToolBoundary);
        Assert.False(result.State.HasActiveTextStream());
        Assert.Contains(result.Effects, effect => effect is StreamingUiEffect.EnsureToolBubble);
    }

    [Fact]
    public void Text_after_toolCallDelta_uses_new_stream()
    {
        var state = new StreamingConversionState();
        state = _engine.Process(state, new StreamingStreamEvent.TextDelta("first")).State;
        state = _engine.Process(
            state,
            new StreamingStreamEvent.ToolCallDelta(new StreamingToolCallDelta(0, "call-1", "read_file", "{}"))).State;

        var result = _engine.Process(state, new StreamingStreamEvent.TextDelta("second"));

        Assert.Contains(result.Effects, effect => effect is StreamingUiEffect.EnsureAssistantBubble);
        Assert.True(result.State.HasActiveTextStream());
    }

    [Fact]
    public void Duplicate_toolCallDelta_only_starts_tool_once()
    {
        var state = new StreamingConversionState();
        var delta = new StreamingToolCallDelta(0, "call-1", "read_file", "{}");

        var first = _engine.Process(state, new StreamingStreamEvent.ToolCallDelta(delta));
        var second = _engine.Process(first.State, new StreamingStreamEvent.ToolCallDelta(delta));

        Assert.Single(first.Effects.OfType<StreamingUiEffect.EnsureToolBubble>());
        Assert.Empty(second.Effects.OfType<StreamingUiEffect.EnsureToolBubble>());
        Assert.Equal(2, second.Effects.OfType<StreamingUiEffect.UpdateToolBubble>().Count());
    }

    [Fact]
    public void TurnFinalize_seals_open_text_stream()
    {
        var state = new StreamingConversionState();
        state = _engine.Process(state, new StreamingStreamEvent.TextDelta("open")).State;

        var result = _engine.Process(state, new StreamingStreamEvent.TurnFinalize());

        Assert.Contains(result.Effects, effect => effect is StreamingUiEffect.SealAssistantBubble);
        Assert.False(result.State.HasActiveTextStream());
        Assert.Null(result.State.ActiveAssistantStreamId);
    }

    [Fact]
    public void ReasoningDelta_before_tool_is_released_at_tool_boundary()
    {
        var state = new StreamingConversionState();
        state = _engine.Process(state, new StreamingStreamEvent.ReasoningDelta("think")).State;

        var result = _engine.Process(
            state,
            new StreamingStreamEvent.ToolCallDelta(new StreamingToolCallDelta(0, "call-1", "read_file", "{}")));

        Assert.Contains(
            result.Effects,
            effect => effect is StreamingUiEffect.ReleaseAssistantBubbleForToolBoundary);
        Assert.False(result.State.HasActiveReasoningStream());
    }

    [Fact]
    public void AssistantMessagePersisted_completes_active_stream()
    {
        var state = new StreamingConversionState();
        state = _engine.Process(state, new StreamingStreamEvent.TextDelta("done")).State;
        var message = ChatMessage.Create(MessageRole.Assistant, "done");

        var result = _engine.Process(state, new StreamingStreamEvent.AssistantMessagePersisted(message));

        Assert.Contains(result.Effects, effect => effect is StreamingUiEffect.CompleteAssistantBubble);
        Assert.Null(result.State.ActiveAssistantStreamId);
    }
}
