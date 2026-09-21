using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

/// <summary>
/// Covers the off-thread persistence variants used by the post-first-paint adopt path. They must
/// behave exactly like their synchronous-on-the-caller counterparts; only the thread the work runs
/// on changes, and that is not observable from the resulting files.
/// </summary>
public sealed class FileStorageOffThreadTests
{
    [Fact]
    public async Task SaveSessionOffThreadAsync_writes_the_same_payload_as_the_normal_path()
    {
        using var temp = new TempDirectoryScope("offthread-save");
        var paths = new TestAppPathProvider(temp.Root);
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("offthread-save")
            .WithMessage(ChatMessage.Create(MessageRole.User, "我是用户"));

        await storage.SaveSessionOffThreadAsync(session);

        var sessionJsonPath = Path.Combine(temp.Root, "sessions", session.Id, "session.json");
        Assert.True(File.Exists(sessionJsonPath));

        // Round-trips through the normal reader, so the off-thread serializer must have produced
        // the exact same shape (including the unescaped CJK literals).
        var loaded = await storage.LoadSessionAsync(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded!.Id);
        Assert.Equal("我是用户", Assert.Single(loaded.Messages).Content);

        var raw = await File.ReadAllTextAsync(sessionJsonPath);
        Assert.Contains("我是用户", raw);
    }

    [Fact]
    public async Task ReplaceConversationDisplayOffThreadAsync_writes_a_readable_display_log()
    {
        using var temp = new TempDirectoryScope("offthread-display");
        var paths = new TestAppPathProvider(temp.Root);
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessionId = "offthread-display";
        var messages = new[]
        {
            ChatMessage.Create(MessageRole.User, "first"),
            ChatMessage.Create(MessageRole.Assistant, "second")
        };

        await storage.ReplaceConversationDisplayOffThreadAsync(sessionId, messages);

        var display = await storage.LoadConversationDisplayAsync(sessionId);
        Assert.Equal(2, display.Count);
        Assert.Equal("first", display[0].Content);
        Assert.Equal("second", display[1].Content);
    }

    [Fact]
    public async Task OffThread_replace_creates_the_session_directories()
    {
        using var temp = new TempDirectoryScope("offthread-dirs");
        var paths = new TestAppPathProvider(temp.Root);
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessionId = "offthread-dirs";

        await storage.ReplaceConversationDisplayOffThreadAsync(
            sessionId,
            [ChatMessage.Create(MessageRole.User, "hello")]);

        // The off-thread directory creation must still leave the full session layout behind, since
        // later writers (transcripts, summaries, evicted) rely on those folders existing.
        var sessionDir = Path.Combine(temp.Root, "sessions", sessionId);
        Assert.True(Directory.Exists(Path.Combine(sessionDir, "summaries")));
        Assert.True(Directory.Exists(Path.Combine(sessionDir, "transcripts")));
        Assert.True(Directory.Exists(Path.Combine(sessionDir, "evicted")));
    }

    [Fact]
    public async Task OffThread_replace_is_a_noop_for_blank_session_ids()
    {
        using var temp = new TempDirectoryScope("offthread-blank");
        var paths = new TestAppPathProvider(temp.Root);
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());

        await storage.ReplaceConversationDisplayOffThreadAsync(
            "   ",
            [ChatMessage.Create(MessageRole.User, "ignored")]);

        Assert.False(Directory.Exists(Path.Combine(temp.Root, "sessions")));
    }

    [Fact]
    public async Task EnsureSessionLogDirectories_is_idempotent_across_repeated_writes()
    {
        using var temp = new TempDirectoryScope("offthread-idempotent");
        var paths = new TestAppPathProvider(temp.Root);
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessionId = "offthread-idempotent";
        var messages = new[] { ChatMessage.Create(MessageRole.User, "hello") };

        // Exercises the cached "already prepared" path on the second call, which must not skip the
        // write itself.
        await storage.ReplaceConversationDisplayOffThreadAsync(sessionId, messages);
        await storage.ReplaceConversationDisplayOffThreadAsync(sessionId, messages);

        Assert.Single(await storage.LoadConversationDisplayAsync(sessionId));
    }

    [Fact]
    public async Task Session_can_be_saved_again_after_being_deleted()
    {
        using var temp = new TempDirectoryScope("offthread-resave");
        var paths = new TestAppPathProvider(temp.Root);
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("re-save")
            .WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        await storage.SaveSessionAsync(session);
        await storage.DeleteSessionAsync(session.Id);

        // Deleting drops the "directories already prepared" marker, so the next write recreates the
        // layout instead of assuming it is still there.
        await storage.SaveSessionOffThreadAsync(session);

        Assert.True(File.Exists(Path.Combine(temp.Root, "sessions", session.Id, "session.json")));
        Assert.True(Directory.Exists(Path.Combine(temp.Root, "sessions", session.Id, "transcripts")));
        Assert.NotNull(await storage.LoadSessionAsync(session.Id));
    }
}
