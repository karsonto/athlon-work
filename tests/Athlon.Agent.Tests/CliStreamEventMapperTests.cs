using Athlon.Agent.Core.Cli;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class CliStreamEventMapperTests
{
    [Fact]
    public void TryMap_TextDelta()
    {
        var frame = CliStreamEventMapper.TryMap(new AgentStreamEvent.TextMessageContent("m1", "hello"));
        Assert.NotNull(frame);
        Assert.Equal(CliSseEventNames.Text, frame.Event);
        var sse = CliStreamEventMapper.Format(frame);
        Assert.Contains("event: text", sse);
        Assert.Contains("\"delta\":\"hello\"", sse);
    }

    [Fact]
    public void TryMap_ToolStartAndOutput()
    {
        var start = CliStreamEventMapper.TryMap(new AgentStreamEvent.ToolCallStart("c1", "file_read", 0));
        Assert.Equal(CliSseEventNames.ToolStart, start!.Event);

        var output = CliStreamEventMapper.TryMap(new AgentStreamEvent.ToolCallOutput("c1", "line\n"));
        Assert.Equal(CliSseEventNames.ToolOutput, output!.Event);

        var end = CliStreamEventMapper.TryMap(new AgentStreamEvent.ToolCallEnd("c1"));
        Assert.Equal(CliSseEventNames.ToolEnd, end!.Event);
    }

    [Fact]
    public void TryMap_IgnoresReasoningAndUsage()
    {
        Assert.Null(CliStreamEventMapper.TryMap(new AgentStreamEvent.ReasoningMessageContent("m1", "think")));
        Assert.Null(CliStreamEventMapper.TryMap(new AgentStreamEvent.RunFinished("s1", "r1")));
    }
}
