using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class SessionHttpLogServiceTests
{
    [Fact]
    public async Task LogInteractionAsync_WritesJsonlUnderSessionHttpFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-http-log-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var service = new SessionHttpLogService(paths, new JsonFileStore(), new AgentRunContextAccessor(), new NoOpLogger());
            await service.LogInteractionAsync(
                "session-1",
                new SessionHttpInteractionLog(
                    DateTimeOffset.UtcNow,
                    "https://example.com/v1/chat/completions",
                    "chat-completion",
                    400,
                    new { model = "test" },
                    "{\"error\":\"bad request\"}",
                    "HTTP 400 Bad Request",
                    120));

            var path = Path.Combine(paths.SessionsPath, "session-1", "http", "interactions.jsonl");
            Assert.True(File.Exists(path));
            var line = await File.ReadAllTextAsync(path);
            Assert.Contains("chat-completion", line, StringComparison.Ordinal);
            Assert.Contains("400", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer sk-", line, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(SessionsPath);
        }

        public string ResolveSkillPath(string path) => Path.Combine(SkillsPath, path);
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
