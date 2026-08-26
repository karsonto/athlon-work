using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class WorkspaceToolBoundaryTests
{
    [Fact]
    public async Task FileRead_AllowsPathOutsideWorkspace()
    {
        var env = await CreateEnvironmentAsync();
        var outsideFile = Path.Combine(env.OutsideRoot, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "secret");

        try
        {
            var result = await env.FileRead.InvokeAsync(
                new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = outsideFile }));

            Assert.True(result.Succeeded);
            Assert.Contains("secret", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FileWrite_RejectsPathOutsideWorkspace()
    {
        var env = await CreateEnvironmentAsync();
        var outsideFile = Path.Combine(env.OutsideRoot, "evil.txt");

        try
        {
            var result = await env.FileWrite.InvokeAsync(
                new ToolInvocation("file_write", new Dictionary<string, string>
                {
                    ["path"] = outsideFile,
                    ["content"] = "nope"
                }));

            Assert.False(result.Succeeded);
            Assert.Equal("Outside workspace", result.Summary);
            Assert.False(File.Exists(outsideFile));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task GrepFiles_AllowsPathOutsideWorkspace()
    {
        var env = await CreateEnvironmentAsync();
        var outsideDir = Path.Combine(env.OutsideRoot, "search");
        Directory.CreateDirectory(outsideDir);
        await File.WriteAllTextAsync(Path.Combine(outsideDir, "a.txt"), "needle");

        try
        {
            var result = await env.GrepFiles.InvokeAsync(
                new ToolInvocation("grep_files", new Dictionary<string, string>
                {
                    ["pattern"] = "needle",
                    ["path"] = outsideDir
                }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("needle", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FileList_AllowsPathOutsideWorkspace()
    {
        var env = await CreateEnvironmentAsync();
        await File.WriteAllTextAsync(Path.Combine(env.OutsideRoot, "outside.txt"), "x");

        try
        {
            var result = await env.FileList.InvokeAsync(
                new ToolInvocation("file_list", new Dictionary<string, string> { ["path"] = env.OutsideRoot }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("outside.txt", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task GlobFiles_AllowsPathOutsideWorkspace()
    {
        var env = await CreateEnvironmentAsync();
        await File.WriteAllTextAsync(Path.Combine(env.OutsideRoot, "hit.cs"), "class Hit {}");

        try
        {
            var result = await env.GlobFiles.InvokeAsync(
                new ToolInvocation("glob_files", new Dictionary<string, string>
                {
                    ["pattern"] = "*.cs",
                    ["path"] = env.OutsideRoot
                }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("hit.cs", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FileRead_AllowsPathInsideWorkspace()
    {
        var env = await CreateEnvironmentAsync();
        await File.WriteAllTextAsync(Path.Combine(env.WorkspaceRoot, "inside.txt"), "hello");

        try
        {
            var result = await env.FileRead.InvokeAsync(
                new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = "inside.txt" }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("hello", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            env.Dispose();
        }
    }

    private static async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-boundary-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var outsideRoot = Path.Combine(root, "outside");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideRoot);
        Directory.CreateDirectory(appDataRoot);

        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var settings = new AppSettings();
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), settings, new TestPathProvider(appDataRoot));
        var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());

        await Task.CompletedTask;
        return new TestEnvironment(
            root,
            workspaceRoot,
            outsideRoot,
            new FileReadTool(guard, audit, settings),
            new FileWriteTool(guard, audit),
            new GrepFilesTool(guard, audit, settings),
            new FileListTool(guard, audit),
            new GlobFilesTool(guard, audit));
    }

    private sealed record TestEnvironment(
        string Root,
        string WorkspaceRoot,
        string OutsideRoot,
        FileReadTool FileRead,
        FileWriteTool FileWrite,
        GrepFilesTool GrepFiles,
        FileListTool FileList,
        GlobFilesTool GlobFiles)
    {
        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
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
