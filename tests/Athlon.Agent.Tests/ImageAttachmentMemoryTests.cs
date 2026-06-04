using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ImageAttachmentMemoryTests
{
    [Fact]
    public void SaveFromFile_persists_without_data_url()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-agent-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        var sessionId = "session-1";
        var source = Path.Combine(root, "source.png");
        File.WriteAllBytes(source, [0x89, 0x50, 0x4E, 0x47]);

        var store = new ImageAttachmentStore(paths);
        var saved = store.SaveFromFile(sessionId, source);

        Assert.Null(saved.DataUrl);
        Assert.False(string.IsNullOrWhiteSpace(saved.LocalPath));
        Assert.True(File.Exists(saved.LocalPath!));
        Assert.StartsWith(Path.Combine(paths.SessionsPath, sessionId, "attachments"), saved.LocalPath!);
    }

    [Fact]
    public void BuildModelMessages_resolves_local_path_to_data_url()
    {
        var file = Path.Combine(Path.GetTempPath(), $"athlon-img-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        try
        {
            var attachment = new ImageAttachment("shot.png", "image/png", DataUrl: null, LocalPath: file);
            var history = new[]
            {
                ChatMessage.Create(MessageRole.User, "see this", imageAttachments: new[] { attachment })
            };

            var messages = AgentRuntime.BuildModelMessages("system", history);
            var user = messages[^1];
            Assert.Equal("user", user.Role);
            Assert.NotNull(user.Content);
            var parts = Assert.IsType<List<object>>(user.Content);
            Assert.Equal(2, parts.Count);
            var imagePart = Assert.IsType<Dictionary<string, object?>>(parts[1]);
            Assert.Equal("image_url", imagePart["type"]);
            var imageUrl = Assert.IsType<Dictionary<string, object?>>(imagePart["image_url"]);
            var url = Assert.IsType<string>(imageUrl["url"]);
            Assert.StartsWith("data:image/png;base64,", url);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void ResolveDataUrl_returns_existing_data_url()
    {
        const string dataUrl = "data:image/png;base64,AA==";
        var attachment = new ImageAttachment("a.png", "image/png", dataUrl);
        Assert.Equal(dataUrl, ImageAttachmentDataUrlResolver.ResolveDataUrl(attachment));
    }

    private sealed class TestAppPathProvider : IAppPathProvider
    {
        public TestAppPathProvider(string root)
        {
            RootPath = root;
            ConfigPath = Path.Combine(root, "config");
            SessionsPath = Path.Combine(root, "sessions");
            AuditPath = Path.Combine(root, "audit");
            LogsPath = Path.Combine(root, "logs");
            CredentialsPath = Path.Combine(root, "credentials");
            SkillsPath = Path.Combine(root, "skills");
        }

        public string RootPath { get; }
        public string ConfigPath { get; }
        public string SessionsPath { get; }
        public string AuditPath { get; }
        public string LogsPath { get; }
        public string CredentialsPath { get; }
        public string SkillsPath { get; }

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(AuditPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CredentialsPath);
            Directory.CreateDirectory(SkillsPath);
        }

        public string ResolveSkillPath(string path) => path;
    }
}
