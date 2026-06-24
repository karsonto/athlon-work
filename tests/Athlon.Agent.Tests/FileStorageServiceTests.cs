using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class FileStorageServiceTests
{
    [Fact]
    public async Task SaveSessionAsync_WritesMetadata()
    {
        using var temp = new TempDirectoryScope("athlon-session");
        var root = temp.Root;
        var paths = new TestAppPathProvider(root);
        var logger = new NoOpLogger();
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("test-session").WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        await storage.SaveSessionAsync(session);

        var sessionJsonPath = Path.Combine(root, "sessions", session.Id, "session.json");
        Assert.True(File.Exists(sessionJsonPath));
        var sessions = await storage.ListSessionsAsync();
        Assert.Contains(sessions, item => item.Id == session.Id);
    }

    [Fact]
    public async Task SaveSessionAsync_WritesChineseLiteralsInSessionJson()
    {
        using var temp = new TempDirectoryScope("athlon-session");
        var root = temp.Root;
        var paths = new TestAppPathProvider(root);
        var logger = new NoOpLogger();
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("chinese-session")
            .WithMessage(ChatMessage.Create(MessageRole.User, "我是用户"));

        await storage.SaveSessionAsync(session);

        var sessionJsonPath = Path.Combine(root, "sessions", session.Id, "session.json");
        var raw = await File.ReadAllTextAsync(sessionJsonPath);

        Assert.Contains("我是用户", raw);
        Assert.DoesNotContain(@"\u6211", raw);
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesSessionDirectoryAndIndexEntry()
    {
        using var temp = new TempDirectoryScope("athlon-session-delete");
        var root = temp.Root;
        var paths = new TestAppPathProvider(root);
        var logger = new NoOpLogger();
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("delete-me")
            .WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        await storage.SaveSessionAsync(session);
        var sessionJsonPath = Path.Combine(root, "sessions", session.Id, "session.json");
        Assert.True(File.Exists(sessionJsonPath));

        await storage.DeleteSessionAsync(session.Id);

        Assert.False(Directory.Exists(Path.Combine(root, "sessions", session.Id)));
        var sessions = await storage.ListSessionsAsync();
        Assert.DoesNotContain(sessions, item => item.Id == session.Id);
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated() => Directory.CreateDirectory(RootPath);

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? path : Path.Combine(SkillsPath, path);
    }
}
