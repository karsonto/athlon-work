using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class FileWriteToolTests
{
    [Fact]
    public async Task FailsWithStructuredError_WhenContentIsEmpty()
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

            Assert.False(result.Succeeded);
            Assert.Equal("Invalid tool arguments", result.Summary);
            var error = DeserializeError(result);
            Assert.Equal("file_write.content.empty", error.Code);
            Assert.Equal("$.content", error.Path);
            Assert.Contains("non-empty", error.Expected, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("empty", error.Actual, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(env.WorkspaceRoot, "empty.txt")));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FailsWithStructuredError_WhenContentMissing()
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
            Assert.Equal("Invalid tool arguments", result.Summary);
            var error = DeserializeError(result);
            Assert.Equal("file_write.content.missing", error.Code);
            Assert.Equal("$.content", error.Path);
            Assert.Contains("missing", error.Actual, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("content", error.Remediation, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FailsWithStructuredError_WhenContentIsNull()
    {
        var env = await CreateEnvironmentAsync();

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation(
                    "file_write",
                    ToolCallArgumentsParser.ParseJson("""{"path":"null-content.txt","content":null}""")));

            Assert.False(result.Succeeded);
            Assert.Equal("Invalid tool arguments", result.Summary);
            var error = DeserializeError(result);
            Assert.Equal("file_write.content.null", error.Code);
            Assert.Equal("$.content", error.Path);
            Assert.Equal("null", error.Actual);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task FailsWithStructuredError_WhenContentIsNotString()
    {
        var env = await CreateEnvironmentAsync();

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation(
                    "file_write",
                    ToolCallArgumentsParser.ParseJson("""{"path":"bad-type.txt","content":42}""")));

            Assert.False(result.Succeeded);
            Assert.Equal("Invalid tool arguments", result.Summary);
            var error = DeserializeError(result);
            Assert.Equal("file_write.content.type_mismatch", error.Code);
            Assert.Equal("$.content", error.Path);
            Assert.Equal("JSON string", error.Expected);
            Assert.Contains("number", error.Actual, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("truncated", error.Remediation, StringComparison.OrdinalIgnoreCase);
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

    private static ToolInvocationError DeserializeError(ToolResult result)
    {
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        var error = JsonSerializer.Deserialize<ToolInvocationError>(
            result.Error!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(error);
        return error!;
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
