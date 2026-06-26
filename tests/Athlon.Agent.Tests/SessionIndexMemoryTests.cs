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
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());

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
    public async Task ListSessionsAsync_excludes_subagent_sessions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-subagent-index-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());

        var parentDir = Path.Combine(paths.SessionsPath, "parent");
        var subDir = Path.Combine(parentDir, "subagents", "default", "sub-1");
        Directory.CreateDirectory(parentDir);
        Directory.CreateDirectory(subDir);

        await WriteSessionJsonAsync(parentDir, "parent", "Parent chat");
        await WriteSessionJsonAsync(subDir, "sub-1", "Sub-agent");

        try
        {
            var sessions = await storage.ListSessionsAsync();

            Assert.Single(sessions);
            Assert.Equal("parent", sessions[0].Id);
            Assert.DoesNotContain(sessions, item => item.Id == "sub-1");
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
    public async Task ListSessionsAsync_filters_subagent_from_cached_index()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-subagent-cache-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());

        var parentDir = Path.Combine(paths.SessionsPath, "parent");
        Directory.CreateDirectory(parentDir);
        await WriteSessionJsonAsync(parentDir, "parent", "Parent chat");

        var subDir = Path.Combine(parentDir, "subagents", "default", "sub-1");
        Directory.CreateDirectory(subDir);
        await WriteSessionJsonAsync(subDir, "sub-1", "Sub-agent");

        var cached = new List<SessionIndexEntry>
        {
            new("parent", "Parent chat", parentDir, DateTimeOffset.UtcNow),
            new("sub-1", "Sub-agent", subDir, DateTimeOffset.UtcNow.AddMinutes(1))
        };
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionsPath, "index.json"),
            JsonSerializer.Serialize(cached, JsonFileStore.Options));

        try
        {
            var sessions = await storage.ListSessionsAsync();

            Assert.Single(sessions);
            Assert.Equal("parent", sessions[0].Id);
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
    public async Task ListSessionsAsync_rebuilds_when_cached_index_misses_new_session()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-stale-index-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());

        var existingDir = Path.Combine(paths.SessionsPath, "existing");
        Directory.CreateDirectory(existingDir);
        await WriteSessionJsonAsync(existingDir, "existing", "Existing chat");
        var cached = new List<SessionIndexEntry>
        {
            new("existing", "Existing chat", existingDir, DateTimeOffset.UtcNow.AddMinutes(-1))
        };
        await File.WriteAllTextAsync(
            Path.Combine(paths.SessionsPath, "index.json"),
            JsonSerializer.Serialize(cached, JsonFileStore.Options));

        var newSession = AgentSession.Create("New Chat");

        try
        {
            await storage.SaveSessionAsync(newSession);

            var sessions = await storage.ListSessionsAsync();

            Assert.Contains(sessions, item => item.Id == "existing");
            Assert.Contains(sessions, item => item.Id == newSession.Id);
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
    public async Task ListSessionsAsync_excludes_top_level_subagent_leak()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-subagent-leak-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());

        var parentDir = Path.Combine(paths.SessionsPath, "parent");
        var subDir = Path.Combine(parentDir, "subagents", "default", "sub-1");
        var leakedDir = Path.Combine(paths.SessionsPath, "sub-1");
        Directory.CreateDirectory(subDir);
        Directory.CreateDirectory(leakedDir);

        await WriteSessionJsonAsync(parentDir, "parent", "Parent chat");
        await WriteSessionJsonAsync(subDir, "sub-1", "Sub-agent");
        await WriteSessionJsonAsync(leakedDir, "sub-1", "Sub-agent");

        try
        {
            var sessions = await storage.ListSessionsAsync();

            Assert.Single(sessions);
            Assert.Equal("parent", sessions[0].Id);
            Assert.DoesNotContain(sessions, item => item.Id == "sub-1");
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
    public async Task SaveSessionAsync_redirects_subagent_to_nested_path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-subagent-redirect-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());

        var parentDir = Path.Combine(paths.SessionsPath, "parent");
        var subDir = Path.Combine(parentDir, "subagents", "default", "sub-1");
        Directory.CreateDirectory(subDir);

        var subSession = new AgentSession(
            "sub-1",
            "Sub-agent",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            Array.Empty<ChatMessage>());

        try
        {
            await storage.SaveSessionAsync(subSession);

            Assert.True(File.Exists(Path.Combine(subDir, "session.json")));
            Assert.False(File.Exists(Path.Combine(paths.SessionsPath, "sub-1", "session.json")));

            var sessions = await storage.ListSessionsAsync();
            Assert.DoesNotContain(sessions, item => item.Id == "sub-1");
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
    public async Task LoadSessionAsync_does_not_load_subagent_session_by_id()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-subagent-load-{Guid.NewGuid():N}");
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore(), new AgentRunContextAccessor());

        var parentDir = Path.Combine(paths.SessionsPath, "parent");
        var subDir = Path.Combine(parentDir, "subagents", "default", "sub-1");
        Directory.CreateDirectory(subDir);
        await WriteSessionJsonAsync(subDir, "sub-1", "Sub-agent");

        try
        {
            var session = await storage.LoadSessionAsync("sub-1");

            Assert.Null(session);
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

    private static async Task WriteSessionJsonAsync(string sessionDir, string id, string title)
    {
        var payload = new
        {
            id,
            title,
            createdAt = DateTimeOffset.UtcNow,
            updatedAt = DateTimeOffset.UtcNow,
            messages = Array.Empty<object>()
        };
        await File.WriteAllTextAsync(
            Path.Combine(sessionDir, "session.json"),
            JsonSerializer.Serialize(payload, JsonFileStore.Options));
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
