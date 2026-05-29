using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ToolArgumentsPathTests
{
    [Fact]
    public void TryGetNormalizedPath_RejectsBackslashFreeInvalidUri()
    {
        var invocation = new ToolInvocation(
            "file_read",
            new Dictionary<string, string> { ["path"] = "https://evil.example/x" });

        Assert.False(ToolArguments.TryGetNormalizedPath(invocation, out _, out var error));
        Assert.False(error.Succeeded);
        Assert.Contains("URI", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetNormalizedPath_StandardizesBackslashes()
    {
        var invocation = new ToolInvocation(
            "file_read",
            new Dictionary<string, string> { ["path"] = @"folder\file.txt" });

        Assert.True(ToolArguments.TryGetNormalizedPath(invocation, out var path, out var error));
        Assert.True(error.Succeeded);
        Assert.Equal("folder/file.txt", path);
    }

    [Fact]
    public async Task FileReadTool_AcceptsNormalizedBackslashPathFromModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-path-norm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "src", "demo.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "hello");

        try
        {
            var appDataRoot = Path.Combine(Path.GetDirectoryName(root)!, ".athlon-agent");
            Directory.CreateDirectory(appDataRoot);
            var context = new ActiveWorkspaceContext();
            context.SetWorkspace(root);
            var guard = new WorkspaceGuard(context, new AppSettings(), new TestPathProvider(appDataRoot));
            var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());
            var tool = new FileReadTool(guard, audit);
            var result = await tool.InvokeAsync(
                new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = @"src\demo.txt" }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("hello", result.Content, StringComparison.Ordinal);
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

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(AuditPath);
        }

        public string ResolveSkillPath(string path) => path;
    }
}
