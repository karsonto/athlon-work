using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

/// <summary>
/// Regression coverage for "delete conversation" failing with
/// <c>IOException: The process cannot access the file 'tasks.json' because it is being used by
/// another process</c> (HResult 0x80070020, ERROR_SHARING_VIOLATION).
///
/// <para>Clearing the plan artifacts writes an empty <c>tasks.json</c> and notifies the task list.
/// That notification kicks off a fire-and-forget refresh which reads the file. While that read
/// holds the handle, the delete removes the session directory. The read used
/// <see cref="File.OpenRead"/> — FileShare.Read, which denies <see cref="FileShare.Delete"/> — so
/// the delete lost the race.</para>
///
/// <para>These tests drive the interleaving deterministically by holding the read handle open
/// across the delete, instead of racing real threads.</para>
/// </summary>
public sealed class SessionDeleteSharingViolationTests
{
    [Fact]
    public void Session_directory_can_be_deleted_while_tasks_json_is_open_for_reading()
    {
        using var temp = new TempDirectoryScope("athlon-share-delete");
        var dir = Path.Combine(temp.Root, "session");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tasks.json");
        File.WriteAllText(path, """{"items":[]}""");

        // This is the handle a background task-list refresh holds mid-deserialization.
        using (var reader = JsonFileStore.OpenReadShared(path))
        {
            Assert.True(reader.CanRead);

            // Must not throw ERROR_SHARING_VIOLATION while the reader is still open.
            var error = Record.Exception(() => Directory.Delete(dir, recursive: true));
            Assert.Null(error);
        }

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void File_OpenRead_would_have_blocked_the_delete()
    {
        // Demonstrates the defect being fixed: the previous implementation's sharing mode rejects
        // the delete. If this ever stops throwing, the regression test above is no longer testing
        // anything meaningful.
        using var temp = new TempDirectoryScope("athlon-share-probe");
        var dir = Path.Combine(temp.Root, "session");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tasks.json");
        File.WriteAllText(path, """{"items":[]}""");

        using (var reader = File.OpenRead(path))
        {
            var error = Record.Exception(() => Directory.Delete(dir, recursive: true));
            Assert.NotNull(error);
            Assert.IsType<IOException>(error);
        }
    }

    [Fact]
    public async Task DeleteSessionAsync_succeeds_while_a_task_list_reader_is_still_open()
    {
        using var temp = new TempDirectoryScope("athlon-delete-race");
        var paths = new TestAppPathProvider(temp.Root);
        var storage = new FileStorageService(
            new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());

        var session = AgentSession.Create("delete-race")
            .WithMessage(ChatMessage.Create(MessageRole.User, "hello"));
        await storage.SaveSessionAsync(session);

        var sessionDir = Path.Combine(temp.Root, "sessions", session.Id);
        var tasksPath = Path.Combine(sessionDir, "tasks.json");
        await new JsonFileStore().SaveAsync(tasksPath, new SessionTaskList());

        // Hold the handle the clearer's notification-triggered refresh would hold, then delete.
        using (var reader = JsonFileStore.OpenReadShared(tasksPath))
        {
            Assert.True(reader.CanRead);
            await storage.DeleteSessionAsync(session.Id);
        }

        Assert.False(Directory.Exists(sessionDir));
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
