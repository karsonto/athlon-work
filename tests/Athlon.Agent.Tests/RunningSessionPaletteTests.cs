using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class RunningSessionPaletteTests
{
    [Fact]
    public void GetColorIndex_is_stable_for_same_session()
    {
        const string sessionId = "session-stable-42";

        var first = RunningSessionPalette.GetColorIndex(sessionId);
        var second = RunningSessionPalette.GetColorIndex(sessionId);

        Assert.Equal(first, second);
        Assert.Equal(RunningSessionPalette.GetBrushResourceKey(sessionId), RunningSessionPalette.GetBrushResourceKey(sessionId));
    }

    [Fact]
    public void GetBrushResourceKey_maps_to_known_palette_keys()
    {
        var key = RunningSessionPalette.GetBrushResourceKey("session-a");

        Assert.Contains(key, RunningSessionPalette.BrushKeys);
    }

    [Fact]
    public void GetColorIndex_can_differ_for_different_sessions()
    {
        var indices = new HashSet<int>();
        foreach (var sessionId in new[] { "s-1", "s-2", "s-3", "s-4", "s-5", "s-6", "s-7", "s-8", "s-9" })
        {
            indices.Add(RunningSessionPalette.GetColorIndex(sessionId));
        }

        Assert.True(indices.Count > 1);
    }
}
