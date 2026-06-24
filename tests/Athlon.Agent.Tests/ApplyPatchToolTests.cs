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
