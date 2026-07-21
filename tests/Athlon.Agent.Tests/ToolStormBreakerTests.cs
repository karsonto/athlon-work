using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class ToolStormBreakerTests
{
    [Fact]
    public void TryInspect_suppresses_third_identical_call()
    {
        var breaker = new ToolStormBreaker(new ToolStormSettings { Threshold = 3 });
        var call = new AgentToolCall("1", "grep_files", new Dictionary<string, string> { ["pattern"] = "foo" });

        Assert.True(breaker.TryInspect(call, out _));
        Assert.True(breaker.TryInspect(call with { Id = "2" }, out _));
        Assert.False(breaker.TryInspect(call with { Id = "3" }, out var reason));
        Assert.Contains("repeat-loop guard", reason);
    }

    [Fact]
    public void TryInspect_treats_equivalent_paths_as_identical()
    {
        var breaker = new ToolStormBreaker(new ToolStormSettings { Threshold = 3 });
        var first = new AgentToolCall("1", "file_read", new Dictionary<string, string> { ["path"] = "a/b" });
        var second = new AgentToolCall("2", "file_read", new Dictionary<string, string> { ["path"] = "./a/b" });
        var third = new AgentToolCall("3", "file_read", new Dictionary<string, string> { ["path"] = @"a\b" });

        Assert.True(breaker.TryInspect(first, out _));
        Assert.True(breaker.TryInspect(second, out _));
        Assert.False(breaker.TryInspect(third, out var reason));
        Assert.Contains("repeat-loop guard", reason);
    }
}
