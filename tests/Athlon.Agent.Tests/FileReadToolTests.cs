using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class FileReadToolTests
{
    [Fact]
    public async Task InvokeAsync_ReadsSmallFileWithLinePrefixes()
    {
        await using var env = await FileReadTestEnvironment.CreateAsync();
        await File.WriteAllTextAsync(env.FilePath, "alpha\nbeta\n");

        var result = await env.Tool.InvokeAsync(
            new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = "demo.txt" }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("1|alpha", result.Content, StringComparison.Ordinal);
        Assert.Contains("2|beta", result.Content, StringComparison.Ordinal);
        Assert.Contains(FileReadLineReader.MetaHeader, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_RejectsFileOverMaxBytes()
    {
        await using var env = await FileReadTestEnvironment.CreateAsync(new FileReadSettings { MaxFileBytes = 16 });
        var bytes = new byte[32];
        await File.WriteAllBytesAsync(env.FilePath, bytes);

        var result = await env.Tool.InvokeAsync(
            new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = "demo.txt" }));

        Assert.False(result.Succeeded);
        Assert.Contains("exceeds", result.Summary + result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_DefaultLimitIs500Lines()
    {
        await using var env = await FileReadTestEnvironment.CreateAsync();
        var lines = string.Join('\n', Enumerable.Range(1, 600).Select(n => $"line-{n}"));
        await File.WriteAllTextAsync(env.FilePath, lines);

        var result = await env.Tool.InvokeAsync(
            new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = "demo.txt" }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("lines_returned: 500", result.Content, StringComparison.Ordinal);
        Assert.Contains("truncated: true", result.Content, StringComparison.Ordinal);
        Assert.Contains("next_offset: 500", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_RespectsExplicitLimitCap()
    {
        await using var env = await FileReadTestEnvironment.CreateAsync();
        await File.WriteAllTextAsync(env.FilePath, string.Join('\n', Enumerable.Range(1, 100).Select(n => $"x-{n}")));

        var result = await env.Tool.InvokeAsync(
            new ToolInvocation("file_read", new Dictionary<string, string>
            {
                ["path"] = "demo.txt",
                ["limit"] = "3000"
            }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("lines_returned: 100", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_TruncatesLongLines()
    {
        await using var env = await FileReadTestEnvironment.CreateAsync(new FileReadSettings { MaxLineChars = 8 });
        await File.WriteAllTextAsync(env.FilePath, new string('x', 40));

        var result = await env.Tool.InvokeAsync(
            new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = "demo.txt" }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(FileReadLineReader.LineTruncatedSuffix, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_StopsWhenResponseCharLimitReached()
    {
        await using var env = await FileReadTestEnvironment.CreateAsync(new FileReadSettings
        {
            MaxResponseChars = 80,
            DefaultLineLimit = 2_000,
            MaxLinesPerCall = 2_000
        });
        await File.WriteAllTextAsync(env.FilePath, string.Join('\n', Enumerable.Range(1, 10).Select(_ => new string('x', 30))));

        var result = await env.Tool.InvokeAsync(
            new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = "demo.txt" }));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Content!.Length <= 200);
        Assert.Contains("truncated: true", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_StartLineEndLineRange()
    {
        await using var env = await FileReadTestEnvironment.CreateAsync();
        await File.WriteAllTextAsync(env.FilePath, "a\nb\nc\nd\n");

        var result = await env.Tool.InvokeAsync(
            new ToolInvocation("file_read", new Dictionary<string, string>
            {
                ["path"] = "demo.txt",
                ["start_line"] = "2",
                ["end_line"] = "3"
            }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("2|b", result.Content, StringComparison.Ordinal);
        Assert.Contains("3|c", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("1|a", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("4|d", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_AllowsAbsolutePathOutsideWorkspace()
    {
        await using var env = await FileReadTestEnvironment.CreateAsync();
        var outsideFile = Path.Combine(Path.GetTempPath(), "athlon-file-read-outside", Guid.NewGuid().ToString("N"), "outside.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideFile)!);
        await File.WriteAllTextAsync(outsideFile, "outside");

        try
        {
            var result = await env.Tool.InvokeAsync(
                new ToolInvocation("file_read", new Dictionary<string, string> { ["path"] = outsideFile }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("outside", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            var outsideRoot = Path.GetDirectoryName(Path.GetDirectoryName(outsideFile)!)!;
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    private sealed class FileReadTestEnvironment : IAsyncDisposable
    {
        private readonly string _root;

        private FileReadTestEnvironment(string root, FileReadTool tool, string filePath)
        {
            _root = root;
            Tool = tool;
            FilePath = filePath;
        }

        public FileReadTool Tool { get; }

        public string FilePath { get; }

        public static async Task<FileReadTestEnvironment> CreateAsync(FileReadSettings? fileRead = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "athlon-file-read", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, "demo.txt");
            await File.WriteAllTextAsync(filePath, "placeholder");

            var appDataRoot = Path.Combine(Path.GetDirectoryName(root)!, ".athlon-agent-test");
            Directory.CreateDirectory(appDataRoot);
            var settings = new AppSettings { FileRead = fileRead ?? new FileReadSettings() };
            var context = new ActiveWorkspaceContext();
            context.SetWorkspace(root);
            var guard = new WorkspaceGuard(context, settings, new TestPathProvider(appDataRoot));
            var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());
            var tool = new FileReadTool(guard, audit, settings);
            return new FileReadTestEnvironment(root, tool, filePath);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
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
}
