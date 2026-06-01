using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class GlobFilesToolTests
{
    [Fact]
    public async Task InvokeAsync_SkipsIgnoredDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-glob-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "dist", "pkg"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));

        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "dist", "pkg", "a.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "src", "a.txt"), "content");

        try
        {
            var tool = CreateTool(workspaceRoot, appDataRoot);
            var result = await tool.InvokeAsync(new ToolInvocation("glob_files", new Dictionary<string, string>
            {
                ["pattern"] = "**/*"
            }));

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Content);
            Assert.Contains("src", result.Content!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dist", result.Content!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static GlobFilesTool CreateTool(string workspaceRoot, string appDataRoot)
    {
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var guard = new WorkspaceGuard(context, new AppSettings(), new TestPathProvider(appDataRoot));
        var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());
        return new GlobFilesTool(guard, audit);
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
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
        public void EnsureCreated() => Directory.CreateDirectory(rootPath);
        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
