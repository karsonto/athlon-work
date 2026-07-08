using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class FileWriteToolTests
{
    [Fact]
    public async Task CreatesEmptyFile_WhenContentIsEmpty()
    {
        var env = await CreateEnvironmentAsync();

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation("file_write", new Dictionary<string, string>
                {
                    ["path"] = "empty.txt",
                    ["content"] = string.Empty
                }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("empty file", result.Summary, StringComparison.OrdinalIgnoreCase);

            var fullPath = Path.Combine(env.WorkspaceRoot, "empty.txt");
            Assert.True(File.Exists(fullPath));
            Assert.Equal(0, new FileInfo(fullPath).Length);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FailsWithHelpfulMessage_WhenContentMissing()
    {
        var env = await CreateEnvironmentAsync();

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation("file_write", new Dictionary<string, string>
                {
                    ["path"] = "missing-content.txt"
                }));

            Assert.False(result.Succeeded);
            Assert.Equal("Missing argument", result.Summary);
            Assert.Contains("empty string", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task WritesWhitespaceOnlyContent()
    {
        var env = await CreateEnvironmentAsync();

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation("file_write", new Dictionary<string, string>
                {
                    ["path"] = "space.txt",
                    ["content"] = " "
                }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(" ", await File.ReadAllTextAsync(Path.Combine(env.WorkspaceRoot, "space.txt")));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FailsWhenPathIsDirectory()
    {
        var env = await CreateEnvironmentAsync();
        var dirPath = Path.Combine(env.WorkspaceRoot, "existing-dir");
        Directory.CreateDirectory(dirPath);

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation("file_write", new Dictionary<string, string>
                {
                    ["path"] = "existing-dir",
                    ["content"] = "nope"
                }));

            Assert.False(result.Succeeded);
            Assert.Equal("Path is a directory", result.Summary);
            Assert.Contains("existing-dir", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task EnrichesOutsideWorkspaceError()
    {
        var env = await CreateEnvironmentAsync();
        var outsideFile = Path.Combine(env.OutsideRoot, "evil.txt");

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation("file_write", new Dictionary<string, string>
                {
                    ["path"] = outsideFile,
                    ["content"] = "nope"
                }));

            Assert.False(result.Succeeded);
            Assert.Equal("Outside workspace", result.Summary);
            Assert.Contains("workspace-relative", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FailsWithHelpfulMessage_WhenPathMissing()
    {
        var env = await CreateEnvironmentAsync();

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation("file_write", new Dictionary<string, string>
                {
                    ["content"] = "hello"
                }));

            Assert.False(result.Succeeded);
            Assert.Equal("Missing argument", result.Summary);
            Assert.Contains("path", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            env.Dispose();
        }
    }

    private static Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-file-write-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var outsideRoot = Path.Combine(root, "outside");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideRoot);
        Directory.CreateDirectory(appDataRoot);

        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var guard = new WorkspaceGuard(
            context,
            new AgentRunContextAccessor(),
            new AppSettings(),
            new TestPathProvider(appDataRoot));
        var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());

        return Task.FromResult(new TestEnvironment(root, workspaceRoot, outsideRoot, new FileWriteTool(guard, audit)));
    }

    private sealed record TestEnvironment(string Root, string WorkspaceRoot, string OutsideRoot, FileWriteTool Tool)
    {
        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
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

        public void EnsureCreated() => Directory.CreateDirectory(rootPath);

        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
