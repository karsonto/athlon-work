using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class FileStorageServiceTests
{
    [Fact]
    public async Task SaveSessionAsync_WritesMarkdownAndMetadata()
    {
        var paths = new AppPathProvider();
        using var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var storage = new FileStorageService(logger, paths, new JsonFileStore());
        var session = AgentSession.Create("test-session").WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        await storage.SaveSessionAsync(session);
        var sessions = await storage.ListSessionsAsync();

        Assert.Contains(sessions, item => item.Id == session.Id);
    }
}
