using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class SessionWorkspaceScopeTests
{
    [Fact]
    public void Enter_ScopedRoot_OverridesActiveWorkspaceContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-scope-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace-a");
        var otherRoot = Path.Combine(root, "workspace-b");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(otherRoot);

        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(otherRoot);
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), new AppSettings(), new TestPathProvider(Path.Combine(root, ".athlon-agent")));

        using (SessionWorkspaceScope.Enter(workspaceRoot, [".git"]))
        {
            Assert.True(guard.IsInsideWorkspace(Path.Combine(workspaceRoot, "file.txt")));
            Assert.False(guard.IsInsideWorkspace(Path.Combine(otherRoot, "file.txt")));
            Assert.Equal([".git"], guard.GetIgnorePatterns());
        }

        Assert.True(guard.IsInsideWorkspace(Path.Combine(otherRoot, "file.txt")));
    }

    [Fact]
    public void Enter_NestedScopes_RestoresPrevious()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-scope-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var context = new ActiveWorkspaceContext();
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), new AppSettings(), new TestPathProvider(Path.Combine(root, ".athlon-agent")));

        using (SessionWorkspaceScope.Enter(first))
        {
            Assert.True(guard.IsInsideWorkspace(Path.Combine(first, "a.txt")));
            using (SessionWorkspaceScope.Enter(second))
            {
                Assert.True(guard.IsInsideWorkspace(Path.Combine(second, "b.txt")));
            }

            Assert.True(guard.IsInsideWorkspace(Path.Combine(first, "a.txt")));
        }
    }

    private sealed class TestPathProvider(string rootPath) : IAppPathProvider
    {
        public string RootPath { get; } = rootPath;
        public string ConfigPath => Path.Combine(rootPath, "config");
        public string SessionsPath => Path.Combine(rootPath, "sessions");
        public string AuditPath => Path.Combine(rootPath, "audit");
        public string LogsPath => Path.Combine(rootPath, "logs");
        public string CredentialsPath => Path.Combine(rootPath, "credentials");
        public string SkillsPath => Path.Combine(rootPath, "skills");
        public void EnsureCreated() { }
        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
