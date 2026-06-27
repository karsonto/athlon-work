using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class GrepFilesToolTests
{
    [Fact]
    public async Task InvokeAsync_SkipsIgnoredDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-grep-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));

        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "node_modules", "pkg", "a.txt"), "needle in ignored folder");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "src", "a.txt"), "needle in source");

        try
        {
            var tool = CreateTool(workspaceRoot, appDataRoot);
            var result = await tool.InvokeAsync(new ToolInvocation("grep_files", new Dictionary<string, string>
            {
                ["pattern"] = "needle"
            }));

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Content);
            Assert.Contains("src", result.Content!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("node_modules", result.Content!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_SkipsTooLargeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-grep-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);

        var largeFile = Path.Combine(workspaceRoot, "large.txt");
        var line = "needle-" + new string('x', 1024);
        await File.WriteAllTextAsync(largeFile, string.Concat(Enumerable.Repeat(line + Environment.NewLine, 4000)));

        try
        {
            var tool = CreateTool(workspaceRoot, appDataRoot);
            var result = await tool.InvokeAsync(new ToolInvocation("grep_files", new Dictionary<string, string>
            {
                ["pattern"] = "needle"
            }));

            Assert.True(result.Succeeded);
            Assert.Equal("No matches found", result.Summary);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_RegexMode_MatchesPattern()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-grep-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));

        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "src", "Foo.cs"), "public class Foo { }");

        try
        {
            var tool = CreateTool(workspaceRoot, appDataRoot);
            var result = await tool.InvokeAsync(new ToolInvocation("grep_files", new Dictionary<string, string>
            {
                ["pattern"] = @"class\s+\w+",
                ["regex"] = "true"
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("Foo.cs", result.Content!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("public class Foo", result.Content!, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_LiteralMode_DoesNotTreatRegexMetacharactersAsPattern()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-grep-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);

        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "sample.txt"), @"class\s+\w+");

        try
        {
            var tool = CreateTool(workspaceRoot, appDataRoot);
            var result = await tool.InvokeAsync(new ToolInvocation("grep_files", new Dictionary<string, string>
            {
                ["pattern"] = @"class\s+\w+"
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("sample.txt", result.Content!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_RegexMode_RejectsInvalidPattern()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-grep-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var tool = CreateTool(workspaceRoot, appDataRoot);
            var result = await tool.InvokeAsync(new ToolInvocation("grep_files", new Dictionary<string, string>
            {
                ["pattern"] = "[unclosed",
                ["regex"] = "true"
            }));

            Assert.False(result.Succeeded);
            Assert.Equal("Invalid regex", result.Summary);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_ParallelScan_RespectsGlobalMaxMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-grep-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);

        for (var i = 0; i < 250; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(workspaceRoot, $"file-{i:D3}.txt"), "needle here");
        }

        try
        {
            var tool = CreateTool(workspaceRoot, appDataRoot);
            var result = await tool.InvokeAsync(new ToolInvocation("grep_files", new Dictionary<string, string>
            {
                ["pattern"] = "needle"
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("Found 200 matches", result.Summary);
            Assert.Equal(200, result.Content!.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_ParallelScan_FindsMatchesAcrossFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-grep-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "alpha"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "beta"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "gamma"));

        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "alpha", "one.txt"), "alpha-marker");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "beta", "two.txt"), "beta-marker");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "gamma", "three.txt"), "gamma-marker");

        try
        {
            var tool = CreateTool(workspaceRoot, appDataRoot);
            var result = await tool.InvokeAsync(new ToolInvocation("grep_files", new Dictionary<string, string>
            {
                ["pattern"] = "marker"
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("Found 3 matches", result.Summary);
            Assert.Contains("alpha", result.Content!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("beta", result.Content!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gamma", result.Content!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static GrepFilesTool CreateTool(string workspaceRoot, string appDataRoot)
    {
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), new AppSettings(), new TestPathProvider(appDataRoot));
        var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());
        return new GrepFilesTool(guard, audit);
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
