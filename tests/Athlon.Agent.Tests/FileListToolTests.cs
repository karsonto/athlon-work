using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class FileListToolTests
{
    [Fact]
    public async Task InvokeAsync_ReturnsWorkspaceRelativePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-list-{Guid.NewGuid():N}");
        var subDir = Path.Combine(root, "docs", "中文");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "报告.txt"), "x");
        try
        {
            var context = new ActiveWorkspaceContext();
            context.SetWorkspace(root);
            var appDataRoot = Path.Combine(Path.GetDirectoryName(root)!, ".athlon-agent-test");
            Directory.CreateDirectory(appDataRoot);
            var guard = new WorkspaceGuard(context, new AppSettings(), new TestPathProvider(appDataRoot));
            var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());
            var tool = new FileListTool(guard, audit);

            var result = await tool.InvokeAsync(new ToolInvocation("file_list", new Dictionary<string, string>()));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("docs/中文/报告.txt", result.Content, StringComparison.Ordinal);
            Assert.Contains("docs/中文/", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }

    private sealed class TestPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated() => Directory.CreateDirectory(AuditPath);

        public string ResolveSkillPath(string path) => path;
    }
}
