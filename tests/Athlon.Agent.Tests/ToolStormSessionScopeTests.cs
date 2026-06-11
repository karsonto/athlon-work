using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class ToolStormSessionScopeTests
{
    [Fact]
    public void SessionToolStormStore_reuses_breaker_for_same_session()
    {
        var store = new SessionToolStormStore();
        var settings = new ToolStormSettings { Threshold = 3 };
        var call = new AgentToolCall("1", "grep_files", new Dictionary<string, string> { ["pattern"] = "foo" });

        var breaker = store.GetOrCreate("session-a", settings);
        Assert.True(breaker.TryInspect(call, out _));
        Assert.True(breaker.TryInspect(call, out _));
        Assert.False(breaker.TryInspect(call, out var reason));
        Assert.Contains("repeat-loop", reason, StringComparison.OrdinalIgnoreCase);

        var sameSessionBreaker = store.GetOrCreate("session-a", settings);
        Assert.False(sameSessionBreaker.TryInspect(call, out _));

        var otherSessionBreaker = store.GetOrCreate("session-b", settings);
        Assert.True(otherSessionBreaker.TryInspect(call, out _));
    }
}
