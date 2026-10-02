using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ApplyPatchToolTests
{
    [Fact]
    public async Task InvokeAsync_AppliesSingleFilePatch()
    {
        var env = await CreateEnvironmentAsync();
        await File.WriteAllTextAsync(Path.Combine(env.WorkspaceRoot, "sample.txt"), "alpha\nbeta\ngamma\n");

        try
        {
            var patch = """
                --- a/sample.txt
                +++ b/sample.txt
                @@ -2,1 +2,1 @@
                -beta
                +BRAVO
                """;

            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("alpha\nBRAVO\ngamma\n", await File.ReadAllTextAsync(Path.Combine(env.WorkspaceRoot, "sample.txt")));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsPatchOutsideWorkspace_WhenPathFilterUsed()
    {
        var env = await CreateEnvironmentAsync();
        var outsideFile = Path.Combine(env.OutsideRoot, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "x\n");
        var relativeOutside = Path.GetRelativePath(env.WorkspaceRoot, outsideFile).Replace('\\', '/');

        try
        {
            var patch = $"""
                --- a/{relativeOutside}
                +++ b/{relativeOutside}
                @@ -1,1 +1,1 @@
                -x
                +y
                """;

            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch,
                ["path"] = relativeOutside
            }));

            Assert.False(result.Succeeded);
            Assert.Equal("Outside workspace", result.Summary);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_ReturnsFailure_WhenHunkDoesNotMatch()
    {
        var env = await CreateEnvironmentAsync();
        await File.WriteAllTextAsync(Path.Combine(env.WorkspaceRoot, "sample.txt"), "alpha\n");

        try
        {
            var patch = """
                --- a/sample.txt
                +++ b/sample.txt
                @@ -1,1 +1,1 @@
                -missing
                +beta
                """;

            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch
            }));

            Assert.False(result.Succeeded);
            Assert.Equal("Patch failed", result.Summary);
            Assert.Equal("alpha\n", await File.ReadAllTextAsync(Path.Combine(env.WorkspaceRoot, "sample.txt")));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_AppliesPatchThatEndsWithNewline()
    {
        var env = await CreateEnvironmentAsync();
        await File.WriteAllTextAsync(Path.Combine(env.WorkspaceRoot, "sample.txt"), "alpha\nbeta\ngamma\n");

        try
        {
            var patch = """
                --- a/sample.txt
                +++ b/sample.txt
                @@ -2,1 +2,1 @@
                -beta
                +BRAVO
                """ + "\n";

            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("alpha\nBRAVO\ngamma\n", await File.ReadAllTextAsync(Path.Combine(env.WorkspaceRoot, "sample.txt")));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_AppliesGitDiffWithTimestamps()
    {
        var env = await CreateEnvironmentAsync();
        var one = Path.Combine(env.WorkspaceRoot, "one.txt");
        var two = Path.Combine(env.WorkspaceRoot, "two.txt");
        await File.WriteAllTextAsync(one, "a\n");
        await File.WriteAllTextAsync(two, "b\n");
        const string stamp = "\t2024-01-01 00:00:00.000000000 +0000";
        var patch =
            "diff --git a/one.txt b/one.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/one.txt" + stamp + "\n" +
            "+++ b/one.txt" + stamp + "\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+A\n" +
            "diff --git a/two.txt b/two.txt\n" +
            "index 3333333..4444444 100644\n" +
            "--- a/two.txt" + stamp + "\n" +
            "+++ b/two.txt" + stamp + "\n" +
            "@@ -1 +1 @@\n" +
            "-b\n" +
            "+B\n";

        try
        {
            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("A\n", await File.ReadAllTextAsync(one));
            Assert.Equal("B\n", await File.ReadAllTextAsync(two));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_LeavesEarlierFileUntouched_WhenLaterHunkDoesNotMatch()
    {
        var env = await CreateEnvironmentAsync();
        var one = Path.Combine(env.WorkspaceRoot, "one.txt");
        var two = Path.Combine(env.WorkspaceRoot, "two.txt");
        await File.WriteAllTextAsync(one, "a\n");
        await File.WriteAllTextAsync(two, "b\n");
        var patch = """
            --- a/one.txt
            +++ b/one.txt
            @@ -1 +1 @@
            -a
            +A
            --- a/two.txt
            +++ b/two.txt
            @@ -1 +1 @@
            -missing
            +B
            """;

        try
        {
            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch
            }));

            Assert.False(result.Succeeded);
            Assert.Equal("Patch failed", result.Summary);
            Assert.Equal("a\n", await File.ReadAllTextAsync(one));
            Assert.Equal("b\n", await File.ReadAllTextAsync(two));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_ReturnsFailure_WhenPatchDoesNotChangeFile()
    {
        var env = await CreateEnvironmentAsync();
        var path = Path.Combine(env.WorkspaceRoot, "sample.txt");
        await File.WriteAllTextAsync(path, "alpha\n");
        var patch = """
            --- a/sample.txt
            +++ b/sample.txt
            @@ -1,1 +1,1 @@
            -alpha
            +alpha
            """;

        try
        {
            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch
            }));

            Assert.False(result.Succeeded);
            Assert.Equal("No files patched", result.Summary);
            Assert.Equal("alpha\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_OmitsTrailingNewline_WhenPatchSaysSo()
    {
        var env = await CreateEnvironmentAsync();
        var path = Path.Combine(env.WorkspaceRoot, "sample.txt");
        await File.WriteAllTextAsync(path, "alpha\n");
        var patch = """
            --- a/sample.txt
            +++ b/sample.txt
            @@ -1,1 +1,1 @@
            -alpha
            +beta
            \ No newline at end of file
            """;

        try
        {
            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("beta", await File.ReadAllTextAsync(path));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_PreservesCrLf()
    {
        var env = await CreateEnvironmentAsync();
        var path = Path.Combine(env.WorkspaceRoot, "sample.txt");
        await File.WriteAllTextAsync(path, "alpha\r\nbeta\r\n");
        var patch = """
            --- a/sample.txt
            +++ b/sample.txt
            @@ -2,1 +2,1 @@
            -beta
            +BRAVO
            """;

        try
        {
            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = patch
            }));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("alpha\r\nBRAVO\r\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_RejectsBeginPatchFormat()
    {
        var env = await CreateEnvironmentAsync();
        try
        {
            var result = await env.Tool.InvokeAsync(new ToolInvocation("apply_patch", new Dictionary<string, string>
            {
                ["patch"] = "*** Begin Patch\n*** Update File: sample.txt\n*** End Patch"
            }));

            Assert.False(result.Succeeded);
            Assert.Contains("unified diff", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            env.Dispose();
        }
    }

    [Fact]
    public void TryParse_RejectsEmptyPatch()
    {
        Assert.False(UnifiedDiffParser.TryParse("   ", out _, out var error));
        Assert.Contains("empty", error!, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-patch-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(root, "workspace");
        var outsideRoot = Path.Combine(root, "outside");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideRoot);
        Directory.CreateDirectory(appDataRoot);

        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), new AppSettings(), new TestPathProvider(appDataRoot));
        var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());

        return new TestEnvironment(root, workspaceRoot, outsideRoot, new ApplyPatchTool(guard, audit));
    }

    private sealed record TestEnvironment(string Root, string WorkspaceRoot, string OutsideRoot, ApplyPatchTool Tool)
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
