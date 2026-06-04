using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class SessionIndexMemoryTests
{
    [Fact]
    public void TryRead_parses_metadata_without_messages_array()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-index-{Guid.NewGuid():N}");
        var sessionDir = Path.Combine(root, "sessions", "session-a");
        Directory.CreateDirectory(sessionDir);
        var sessionJson = Path.Combine(sessionDir, "session.json");
        var payload = new
        {
            id = "session-a",
            title = "大对话",
            createdAt = DateTimeOffset.Parse("2025-06-01T00:00:00Z"),
            updatedAt = DateTimeOffset.Parse("2025-06-02T00:00:00Z"),
            messages = new[] { new { id = "m1", role = "user", content = new string('x', 50_000) } }
        };
        File.WriteAllText(sessionJson, JsonSerializer.Serialize(payload, JsonFileStore.Options));

        try
        {
            var entry = SessionJsonIndexReader.TryRead(sessionJson);

            Assert.NotNull(entry);
            Assert.Equal("session-a", entry!.Id);
            Assert.Equal("大对话", entry.Title);
            Assert.Equal(sessionDir, entry.Path);
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
    public async Task ListSessionsAsync_uses_index_without_loading_message_bodies()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-list-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore());

        for (var i = 0; i < 3; i++)
        {
            var sessionDir = Path.Combine(paths.SessionsPath, $"session-{i}");
            Directory.CreateDirectory(sessionDir);
            var payload = new
            {
                id = $"session-{i}",
                title = $"chat-{i}",
                createdAt = DateTimeOffset.UtcNow,
                updatedAt = DateTimeOffset.UtcNow.AddMinutes(i),
                messages = new[] { new { id = "m1", role = "user", content = new string('z', 20_000) } }
            };
            await File.WriteAllTextAsync(
                Path.Combine(sessionDir, "session.json"),
                JsonSerializer.Serialize(payload, JsonFileStore.Options));
        }

        try
        {
            var sessions = await storage.ListSessionsAsync();

            Assert.Equal(3, sessions.Count);
            Assert.Equal("session-2", sessions[0].Id);
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
    public void SanitizeSession_strips_data_url_when_local_path_present()
    {
        var attachment = new ImageAttachment(
            "a.png",
            "image/png",
            "data:image/png;base64,QUJD",
            @"C:\temp\a.png");
        var message = ChatMessage.Create(MessageRole.User, "hi", imageAttachments: new[] { attachment });
        var session = AgentSession.Create("s").WithMessage(message);

        var sanitized = ChatMessageMemorySanitizer.SanitizeSession(session);

        Assert.Null(sanitized.Messages[0].ImageAttachments![0].DataUrl);
        Assert.Equal(@"C:\temp\a.png", sanitized.Messages[0].ImageAttachments![0].LocalPath);
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
