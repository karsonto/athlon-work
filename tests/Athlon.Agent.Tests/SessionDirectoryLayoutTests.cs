using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

/// <summary>
/// Covers the nested sub-agent resolution cache. These tests share process-wide static state with
/// <see cref="SessionSwitchProfilerTests"/>, so they join that collection and each one invalidates
/// only its own sessions root (never a bare <c>InvalidateNestedIndex()</c>, which would race
/// parallel collections).
/// </summary>
[Collection(SessionSwitchProfilerCollection.Name)]
public sealed class SessionDirectoryLayoutTests
{
    [Fact]
    public void ResolveEffectiveSessionDirectory_finds_nested_sub_agent_directory()
    {
        using var temp = new TempDirectoryScope("layout-nested");
        var sessions = Path.Combine(temp.Root, "sessions");
        var nested = Path.Combine(sessions, "parent-1", "subagents", "default", "child-1");
        Directory.CreateDirectory(nested);

        var resolved = SessionDirectoryLayout.ResolveEffectiveSessionDirectory(
            sessions,
            "child-1",
            Path.Combine(sessions, "child-1"),
            AgentRunKind.Root);

        Assert.Equal(nested, resolved);
    }

    [Fact]
    public void ResolveEffectiveSessionDirectory_leaves_top_level_sessions_alone()
    {
        using var temp = new TempDirectoryScope("layout-top");
        var sessions = Path.Combine(temp.Root, "sessions");
        var topLevel = Path.Combine(sessions, "solo");
        Directory.CreateDirectory(topLevel);

        var resolved = SessionDirectoryLayout.ResolveEffectiveSessionDirectory(
            sessions,
            "solo",
            topLevel,
            AgentRunKind.Root);

        Assert.Equal(topLevel, resolved);
    }

    [Fact]
    public void ResolveEffectiveSessionDirectory_trusts_a_sub_agent_context()
    {
        using var temp = new TempDirectoryScope("layout-subagent-context");
        var sessions = Path.Combine(temp.Root, "sessions");
        var nested = Path.Combine(sessions, "parent-2", "subagents", "default", "child-2");
        Directory.CreateDirectory(nested);

        // A sub-agent already resolved its own directory; re-probing would find the same path and
        // is exactly the redundant work this method exists to avoid.
        var before = SessionDirectoryLayout.ProbeCount;
        var resolved = SessionDirectoryLayout.ResolveEffectiveSessionDirectory(
            sessions,
            "child-2",
            nested,
            AgentRunKind.SubAgent);

        Assert.Equal(nested, resolved);
        Assert.Equal(before, SessionDirectoryLayout.ProbeCount);
    }

    [Fact]
    public void Nested_index_is_built_once_and_reused()
    {
        using var temp = new TempDirectoryScope("layout-cache");
        var sessions = Path.Combine(temp.Root, "sessions");
        Directory.CreateDirectory(Path.Combine(sessions, "parent-3", "subagents", "default", "child-3"));
        Directory.CreateDirectory(Path.Combine(sessions, "parent-4", "subagents", "default", "child-4"));

        try
        {
            SessionDirectoryLayout.InvalidateNestedIndex(sessions);
            var before = SessionDirectoryLayout.ProbeCount;

            Assert.NotNull(SessionDirectoryLayout.TryFindNestedSubAgentDirectory(sessions, "child-3"));
            Assert.NotNull(SessionDirectoryLayout.TryFindNestedSubAgentDirectory(sessions, "child-4"));
            Assert.True(SessionDirectoryLayout.IsNestedSubAgentSessionId(sessions, "child-3"));
            Assert.Contains("child-4", SessionDirectoryLayout.CollectNestedSubAgentSessionIds(sessions));

            // Four lookups against one sessions root must cost exactly one enumeration; that is
            // the whole point of the index.
            Assert.Equal(before + 1, SessionDirectoryLayout.ProbeCount);
        }
        finally
        {
            SessionDirectoryLayout.InvalidateNestedIndex(sessions);
        }
    }

    [Fact]
    public void InvalidateNestedIndex_makes_a_new_sub_agent_visible()
    {
        using var temp = new TempDirectoryScope("layout-invalidate");
        var sessions = Path.Combine(temp.Root, "sessions");
        Directory.CreateDirectory(sessions);

        try
        {
            // Prime the cache with "no sub-agents exist".
            Assert.False(SessionDirectoryLayout.IsNestedSubAgentSessionId(sessions, "late-child"));

            var late = Path.Combine(sessions, "parent-5", "subagents", "default", "late-child");
            Directory.CreateDirectory(late);

            // Without invalidation the cache would keep answering "not a sub-agent" until TTL.
            SessionDirectoryLayout.InvalidateNestedIndex(sessions);

            Assert.Equal(
                late,
                SessionDirectoryLayout.TryFindNestedSubAgentDirectory(sessions, "late-child"));
        }
        finally
        {
            SessionDirectoryLayout.InvalidateNestedIndex(sessions);
        }
    }

    [Fact]
    public void ResetProbeStats_clears_the_counters()
    {
        using var temp = new TempDirectoryScope("layout-stats");
        var sessions = Path.Combine(temp.Root, "sessions");
        Directory.CreateDirectory(sessions);

        try
        {
            SessionDirectoryLayout.InvalidateNestedIndex(sessions);
            SessionDirectoryLayout.GetNestedIndex(sessions);
            Assert.True(SessionDirectoryLayout.ProbeCount > 0);

            SessionDirectoryLayout.ResetProbeStats();
            Assert.Equal(0, SessionDirectoryLayout.ProbeCount);
            Assert.Equal(TimeSpan.Zero, SessionDirectoryLayout.ProbeElapsed);
        }
        finally
        {
            SessionDirectoryLayout.InvalidateNestedIndex(sessions);
        }
    }
}
