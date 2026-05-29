using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class WorkspaceGuardTests
{
    [Fact]
    public void IsInsideWorkspace_AllowsWorkspaceAndAthlonAgentRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-guard-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(appDataRoot);

        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var settings = new AppSettings();
        var guard = new WorkspaceGuard(context, settings, new TestPathProvider(appDataRoot));

        var workspaceFile = Path.Combine(workspaceRoot, "a.txt");
        var skillFile = Path.Combine(appDataRoot, "skills", "demo", "SKILL.md");
        var outsideFile = Path.Combine(root, "other", "x.txt");

        Assert.True(guard.IsInsideWorkspace(workspaceFile));
        Assert.True(guard.IsInsideWorkspace(skillFile));
        Assert.False(guard.IsInsideWorkspace(outsideFile));
    }

    [Fact]
    public void Normalize_AcceptsForwardSlashRelativePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-guard-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
        File.WriteAllText(Path.Combine(workspaceRoot, "src", "demo.txt"), "ok");

        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var guard = new WorkspaceGuard(context, new AppSettings(), new TestPathProvider(Path.Combine(root, ".athlon-agent")));

        var fullPath = guard.Normalize("src/demo.txt");
        Assert.True(File.Exists(fullPath));
        Assert.True(guard.IsInsideWorkspace(fullPath));
    }

    [Fact]
    public void IsInsideWorkspace_WithoutConfiguredWorkspace_AllowsAthlonAgentOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-guard-{Guid.NewGuid():N}");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(appDataRoot);

        var context = new ActiveWorkspaceContext();
        var settings = new AppSettings();
        var guard = new WorkspaceGuard(context, settings, new TestPathProvider(appDataRoot));

        Assert.True(guard.IsInsideWorkspace(Path.Combine(appDataRoot, "skills", "demo", "SKILL.md")));
        Assert.False(guard.IsInsideWorkspace(Path.Combine(root, "workspace", "a.txt")));
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
