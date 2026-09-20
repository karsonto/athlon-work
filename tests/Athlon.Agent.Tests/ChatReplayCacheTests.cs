using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using System.Globalization;

namespace Athlon.Agent.Tests;

/// <summary>
/// Serializes tests that touch the process-wide <see cref="MarkdownHtmlRenderer"/> memoization state
/// (cache + render counter).
/// </summary>
public static class ChatReplayCacheCollection
{
    public const string Name = "chat-replay-cache";
}

[CollectionDefinition(ChatReplayCacheCollection.Name, DisableParallelization = true)]
public sealed class ChatReplayCacheCollectionDefinition;

[Collection(ChatReplayCacheCollection.Name)]
public sealed class ChatReplayCacheTests
{
    private static ChatMessageViewModel Assistant(string content) =>
        new(ChatMessage.Create(MessageRole.Assistant, content));

    private static ChatMessageViewModel User(string content) =>
        new(ChatMessage.Create(MessageRole.User, content));

    [Fact]
    public void TryGet_returns_shards_for_matching_revision()
    {
        var cache = new ChatReplaySnapshotCache();
        var batches = new IReadOnlyList<string>[] { new[] { "a" }, new[] { "b" } };

        cache.Set("s1", "rev-1", batches);

        Assert.True(cache.TryGet("s1", "rev-1", out var hit));
        Assert.Same(batches, hit);
    }

    [Fact]
    public void TryGet_misses_when_revision_changes()
    {
        var cache = new ChatReplaySnapshotCache();
        cache.Set("s1", "rev-1", [new[] { "a" }]);

        Assert.False(cache.TryGet("s1", "rev-2", out var miss));
        Assert.Empty(miss);
    }

    [Fact]
    public void Set_evicts_least_recently_used_beyond_capacity()
    {
        var cache = new ChatReplaySnapshotCache(capacity: 2);
        cache.Set("s1", "r", [new[] { "1" }]);
        cache.Set("s2", "r", [new[] { "2" }]);

        // Touch s1 so s2 becomes the coldest, then overflow.
        Assert.True(cache.TryGet("s1", "r", out _));
        cache.Set("s3", "r", [new[] { "3" }]);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("s1", "r", out _));
        Assert.False(cache.TryGet("s2", "r", out _));
        Assert.True(cache.TryGet("s3", "r", out _));
    }

    [Fact]
    public void Remove_drops_the_session()
    {
        var cache = new ChatReplaySnapshotCache();
        cache.Set("s1", "r", [new[] { "1" }]);

        cache.Remove("s1");

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("s1", "r", out _));
    }

    [Fact]
    public void Disabled_cache_never_stores_or_hits()
    {
        var cache = new ChatReplaySnapshotCache { Enabled = false };

        cache.Set("s1", "r", [new[] { "1" }]);

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("s1", "r", out _));
    }

    [Fact]
    public void Revision_is_stable_for_identical_input()
    {
        var messages = new[] { Assistant("hello"), User("world") };

        var a = ChatReplayRevision.Compute(messages, showToolCalls: true, activitySource: null, planRun: null);
        var b = ChatReplayRevision.Compute(messages, showToolCalls: true, activitySource: null, planRun: null);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Revision_changes_when_message_content_changes()
    {
        var before = ChatReplayRevision.Compute(
            new[] { Assistant("hello") }, showToolCalls: true, activitySource: null, planRun: null);
        var after = ChatReplayRevision.Compute(
            new[] { Assistant("hello!") }, showToolCalls: true, activitySource: null, planRun: null);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Revision_changes_when_show_tool_calls_or_plan_changes()
    {
        var messages = new[] { Assistant("hello") };
        var baseRev = ChatReplayRevision.Compute(messages, showToolCalls: true, activitySource: null, planRun: null);

        var noTools = ChatReplayRevision.Compute(messages, showToolCalls: false, activitySource: null, planRun: null);
        Assert.NotEqual(baseRev, noTools);

        var withPlan = ChatReplayRevision.Compute(
            messages,
            showToolCalls: true,
            activitySource: null,
            planRun: new PlanRun { Id = "run-1", SessionId = "s1", PlanMarkdown = "# Plan" });
        Assert.NotEqual(baseRev, withPlan);
    }

    [Fact]
    public void Revision_prefers_activity_source_when_present()
    {
        var messages = new[] { Assistant("shown") };
        var activity = new[] { ChatMessage.Create(MessageRole.User, "from-activity") };

        var fromMessages = ChatReplayRevision.Compute(messages, showToolCalls: true, activitySource: null, planRun: null);
        var fromActivity = ChatReplayRevision.Compute(messages, showToolCalls: true, activitySource: activity, planRun: null);

        Assert.NotEqual(fromMessages, fromActivity);
    }

    [Fact]
    public void Revision_changes_when_a_message_is_appended()
    {
        var before = ChatReplayRevision.Compute(new[] { Assistant("a") }, true, null, null);
        var after = ChatReplayRevision.Compute(new[] { Assistant("a"), User("b") }, true, null, null);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Revision_changes_when_ui_culture_changes()
    {
        var messages = new[] { Assistant("hello") };
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
            var zh = ChatReplayRevision.Compute(messages, true, null, null);

            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            var en = ChatReplayRevision.Compute(messages, true, null, null);

            Assert.NotEqual(zh, en);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void ToHtmlFragment_memoizes_identical_markdown()
    {
        MarkdownHtmlRenderer.ResetRenderCacheForTests();
        var markdown = "unique-" + Guid.NewGuid().ToString("N") + "\n\n**bold**";

        var first = MarkdownHtmlRenderer.ToHtmlFragment(markdown);
        var rendersAfterFirst = MarkdownHtmlRenderer.RenderInvocationCount;

        var second = MarkdownHtmlRenderer.ToHtmlFragment(markdown);

        Assert.Equal(first, second);
        Assert.True(rendersAfterFirst >= 1);
        Assert.Equal(rendersAfterFirst, MarkdownHtmlRenderer.RenderInvocationCount);
    }

    /// <summary>
    /// Acceptance for the replay cache: once the shards are cached, a second switch with the same
    /// revision resolves from the cache and never calls <c>BuildReplayEvents</c>, so the markdown
    /// pipeline does not run again.
    /// </summary>
    [Fact]
    public void Cache_hit_skips_the_replay_build()
    {
        MarkdownHtmlRenderer.ResetRenderCacheForTests();
        var messages = new[] { Assistant("# Title\n\nSome **bold** text.") };
        var cache = new ChatReplaySnapshotCache();

        var revision = ChatReplayRevision.Compute(messages, showToolCalls: true, activitySource: null, planRun: null);
        var events = ChatEventSerializer.BuildReplayEvents(messages, showToolCalls: true, includeReset: true);
        var rendersAfterBuild = MarkdownHtmlRenderer.RenderInvocationCount;
        Assert.True(rendersAfterBuild > 0, "building the replay must render markdown at least once");
        cache.Set("s1", revision, new IReadOnlyList<string>[] { events });

        var secondRevision = ChatReplayRevision.Compute(messages, showToolCalls: true, activitySource: null, planRun: null);
        Assert.Equal(revision, secondRevision);
        Assert.True(cache.TryGet("s1", secondRevision, out var batches));
        Assert.Single(batches);
        Assert.Equal(rendersAfterBuild, MarkdownHtmlRenderer.RenderInvocationCount);
    }
}
