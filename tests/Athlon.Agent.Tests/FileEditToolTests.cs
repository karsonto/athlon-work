using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class FileEditToolTests
{
    [Fact]
    public async Task InvokeAsync_StripsFileReadLinePrefixesFromOldText()
    {
        var root = CreateWorkspaceRoot();
        var file = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(file, "alpha\nbeta\ngamma");

        try
        {
            var tool = CreateTool(root);
            var result = await tool.InvokeAsync(new ToolInvocation("file_edit", new Dictionary<string, string>
            {
                ["path"] = "sample.txt",
                ["old_text"] = "2|beta",
                ["new_text"] = "BRAVO"
            }));

            Assert.True(result.Succeeded);
            Assert.Equal("alpha\nBRAVO\ngamma", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task InvokeAsync_NormalizesLfOldTextAgainstCrlfFile()
    {
        var root = CreateWorkspaceRoot();
        var file = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(file, "line one\r\nline two");

        try
        {
            var tool = CreateTool(root);
            var result = await tool.InvokeAsync(new ToolInvocation("file_edit", new Dictionary<string, string>
            {
                ["path"] = "sample.txt",
                ["old_text"] = "line one\nline two",
                ["new_text"] = "merged"
            }));

            Assert.True(result.Succeeded);
            Assert.Equal("merged", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenTextMissing_ReturnsHelpfulError()
    {
        var root = CreateWorkspaceRoot();
        var file = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(file, "hello");

        try
        {
            var tool = CreateTool(root);
            var result = await tool.InvokeAsync(new ToolInvocation("file_edit", new Dictionary<string, string>
            {
                ["path"] = "sample.txt",
                ["old_text"] = "1|missing",
                ["new_text"] = "x"
            }));

            Assert.False(result.Succeeded);
            Assert.Contains("line-number prefixes", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string CreateWorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-file-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FileEditTool CreateTool(string workspaceRoot)
    {
        var appDataRoot = Path.Combine(Path.GetDirectoryName(workspaceRoot)!, ".athlon-agent");
        Directory.CreateDirectory(appDataRoot);
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), new AppSettings(), new TestPathProvider(appDataRoot));
        var audit = new AuditLogService(new NoOpLogger(), new TestPathProvider(appDataRoot), new JsonFileStore());
        return new FileEditTool(guard, audit);
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
