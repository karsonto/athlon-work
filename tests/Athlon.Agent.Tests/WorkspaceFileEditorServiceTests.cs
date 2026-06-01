using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class WorkspaceFileEditorServiceTests
{
    [Fact]
    public async Task TryOpenAsync_RejectsOutsideWorkspace()
    {
        var root = CreateTempRoot();
        try
        {
            var workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var outside = Path.Combine(root, "outside.txt");
            await File.WriteAllTextAsync(outside, "hello");

            var service = CreateService(workspaceRoot, root);
            var result = await service.TryOpenAsync(outside);

            Assert.False(result.Succeeded);
            Assert.Contains("工作区", result.ErrorMessage!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TryOpenAsync_RejectsBinaryExtension()
    {
        var root = CreateTempRoot();
        try
        {
            var workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var image = Path.Combine(workspaceRoot, "shot.png");
            await File.WriteAllTextAsync(image, "not really png");

            var service = CreateService(workspaceRoot, root);
            var result = await service.TryOpenAsync(image);

            Assert.False(result.Succeeded);
            Assert.Contains("无法在编辑器中打开", result.ErrorMessage!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TryOpenAsync_AndSaveAsync_RoundTripTextFile()
    {
        var root = CreateTempRoot();
        try
        {
            var workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var file = Path.Combine(workspaceRoot, "note.txt");
            await File.WriteAllTextAsync(file, "v1");

            var service = CreateService(workspaceRoot, root);
            var open = await service.TryOpenAsync(file);
            Assert.True(open.Succeeded);
            Assert.Equal("v1", open.Content);

            var save = await service.SaveAsync(file, "v2");
            Assert.True(save.Succeeded);
            Assert.Equal("v2", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), $"athlon-editor-{Guid.NewGuid():N}");

    private static WorkspaceFileEditorService CreateService(string workspaceRoot, string appDataRoot)
    {
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var guard = new WorkspaceGuard(context, new AppSettings(), new TestPathProvider(appDataRoot));
        return new WorkspaceFileEditorService(guard, new AppSettings());
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
