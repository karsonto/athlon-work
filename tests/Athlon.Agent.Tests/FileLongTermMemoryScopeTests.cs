using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Memory;

namespace Athlon.Agent.Tests;

public sealed class FileLongTermMemoryScopeTests
{
    [Fact]
    public async Task ReadWrite_Isolates_BySessionId()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new TestPathProvider(root);
            paths.EnsureCreated();
            var workspace = new ActiveWorkspaceContext();
            workspace.SetWorkspace(Path.Combine(root, "ws"), WorkspaceKind.Local, "proj-a", "ws");

            var session = new ActiveAgentSessionContext();
            var memory = CreateMemory(paths, workspace, session);

            session.SetSession("session-1");
            await memory.WriteCuratedAsync("memory for session one");
            await memory.AppendDailyAsync("daily one\n");

            session.SetSession("session-2");
            await memory.WriteCuratedAsync("memory for session two");

            session.SetSession("session-1");
            Assert.Equal("memory for session one", await memory.ReadCuratedAsync());
            Assert.Contains("daily one", await memory.ReadDailyAsync(DateTime.UtcNow));

            session.SetSession("session-2");
            Assert.Equal("memory for session two", await memory.ReadCuratedAsync());
            Assert.Equal(string.Empty, await memory.ReadDailyAsync(DateTime.UtcNow));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ReadWrite_Isolates_ByWorkspaceKey()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new TestPathProvider(root);
            paths.EnsureCreated();
            var workspace = new ActiveWorkspaceContext();
            var session = new ActiveAgentSessionContext();
            session.SetSession("shared-session");
            var memory = CreateMemory(paths, workspace, session);

            workspace.SetWorkspace(Path.Combine(root, "a"), WorkspaceKind.Local, "ws-a", "a");
            await memory.WriteCuratedAsync("project A");

            workspace.SetWorkspace(Path.Combine(root, "b"), WorkspaceKind.Local, "ws-b", "b");
            await memory.WriteCuratedAsync("project B");

            workspace.SetWorkspace(Path.Combine(root, "a"), WorkspaceKind.Local, "ws-a", "a");
            Assert.Equal("project A", await memory.ReadCuratedAsync());

            workspace.SetWorkspace(Path.Combine(root, "b"), WorkspaceKind.Local, "ws-b", "b");
            Assert.Equal("project B", await memory.ReadCuratedAsync());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task WithoutWorkspace_ReadEmpty_WriteNoOp()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new TestPathProvider(root);
            paths.EnsureCreated();
            var workspace = new ActiveWorkspaceContext();
            var session = new ActiveAgentSessionContext();
            session.SetSession("sess");
            var memory = CreateMemory(paths, workspace, session);

            Assert.False(memory.HasActiveScope);
            Assert.Equal(string.Empty, await memory.ReadCuratedAsync());
            await memory.WriteCuratedAsync("should not persist");
            await memory.AppendDailyAsync("nope");
            Assert.Equal(string.Empty, await memory.ReadCuratedAsync());
            Assert.Empty(Directory.Exists(Path.Combine(root, "memory"))
                ? Directory.GetDirectories(Path.Combine(root, "memory"), "*", SearchOption.AllDirectories)
                : []);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task DeleteCurrentSessionMemory_RemovesDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new TestPathProvider(root);
            paths.EnsureCreated();
            var workspace = new ActiveWorkspaceContext();
            workspace.SetWorkspace(Path.Combine(root, "ws"), WorkspaceKind.Local, "proj-del", "ws");
            var session = new ActiveAgentSessionContext();
            session.SetSession("sess-del");
            var memory = CreateMemory(paths, workspace, session);

            await memory.WriteCuratedAsync("to delete");
            var dir = MemoryScopeResolver.BuildMemoryDir(root, "memory", "proj-del", "sess-del");
            Assert.True(Directory.Exists(dir));
            Assert.True(File.Exists(Path.Combine(dir, "MEMORY.md")));

            await memory.DeleteCurrentSessionMemoryAsync();

            Assert.False(Directory.Exists(dir));
            Assert.Equal(string.Empty, await memory.ReadCuratedAsync());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task DeleteSessionMemory_ByExplicitIds_LeavesOtherSessions()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new TestPathProvider(root);
            paths.EnsureCreated();
            var workspace = new ActiveWorkspaceContext();
            workspace.SetWorkspace(Path.Combine(root, "ws"), WorkspaceKind.Local, "proj-x", "ws");
            var session = new ActiveAgentSessionContext();
            var memory = CreateMemory(paths, workspace, session);

            session.SetSession("keep");
            await memory.WriteCuratedAsync("keep me");
            session.SetSession("drop");
            await memory.WriteCuratedAsync("drop me");

            await memory.DeleteSessionMemoryAsync("proj-x", "drop");

            Assert.False(Directory.Exists(MemoryScopeResolver.BuildMemoryDir(root, "memory", "proj-x", "drop")));
            session.SetSession("keep");
            Assert.Equal("keep me", await memory.ReadCuratedAsync());
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static FileLongTermMemory CreateMemory(
        TestPathProvider paths,
        ActiveWorkspaceContext workspace,
        ActiveAgentSessionContext session) =>
        new(paths, workspace, session, new AppSettings(), new NoOpLogger());

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "athlon-memory-scope-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private sealed class TestPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated() => Directory.CreateDirectory(RootPath);

        public string ResolveSkillPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
