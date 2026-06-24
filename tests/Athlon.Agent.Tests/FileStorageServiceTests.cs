using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class FileStorageServiceTests
{
    [Fact]
    public async Task SaveSessionAsync_WritesMarkdownAndMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-session-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        var logger = new NoOpLogger();
        var storage = new FileStorageService(logger, paths, new JsonFileStore());
        var session = AgentSession.Create("test-session").WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        try
        {
            await storage.SaveSessionAsync(session);

            var sessionJsonPath = Path.Combine(root, "sessions", session.Id, "session.json");
            Assert.True(File.Exists(sessionJsonPath));
            var sessions = await storage.ListSessionsAsync();
            Assert.Contains(sessions, item => item.Id == session.Id);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveSessionAsync_WritesChineseLiteralsInSessionJson()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-session-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        var logger = new NoOpLogger();
        var storage = new FileStorageService(logger, paths, new JsonFileStore());
        var session = AgentSession.Create("chinese-session")
            .WithMessage(ChatMessage.Create(MessageRole.User, "我是用户"));

        try
        {
            await storage.SaveSessionAsync(session);

            var sessionJsonPath = Path.Combine(root, "sessions", session.Id, "session.json");
            var raw = await File.ReadAllTextAsync(sessionJsonPath);

            Assert.Contains("我是用户", raw);
            Assert.DoesNotContain(@"\u6211", raw);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
        public void Dispose() { }
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(root, "config");
        public string SessionsPath => Path.Combine(root, "sessions");
        public string AuditPath => Path.Combine(root, "audit");
        public string LogsPath => Path.Combine(root, "logs");
        public string CredentialsPath => Path.Combine(root, "credentials");
        public string SkillsPath => Path.Combine(root, "skills");

        public void EnsureCreated() => Directory.CreateDirectory(root);

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? path : Path.Combine(SkillsPath, path);
    }
}
