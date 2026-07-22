using System.Runtime.CompilerServices;
using System.Text;
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

            var appDataRoot = Path.Combine(Path.GetTempPath(), $".athlon-agent-editor-{Guid.NewGuid():N}");
            Directory.CreateDirectory(appDataRoot);
            var service = CreateLocalService(workspaceRoot, appDataRoot);
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
    public async Task TryOpenAsync_OpensNonTextExtensionAsText()
    {
        var root = CreateTempRoot();
        try
        {
            var workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var image = Path.Combine(workspaceRoot, "shot.png");
            await File.WriteAllTextAsync(image, "not really png");

            var service = CreateLocalService(workspaceRoot, root);
            var result = await service.TryOpenAsync(image);

            Assert.True(result.Succeeded);
            Assert.Equal("not really png", result.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TryOpenAsync_Ssh_OpensNonTextExtensionAsText()
    {
        var remoteRoot = "/home/u/racing";
        var remoteFile = "/home/u/racing/app-1.0.0.jar";
        var client = new InMemorySshClient(remoteRoot)
        {
            Files = { [remoteFile] = "PK\u0000fake-jar" }
        };
        var service = CreateSshService(remoteRoot, client);

        var result = await service.TryOpenAsync(remoteFile);

        Assert.True(result.Succeeded);
        Assert.Equal("PK\u0000fake-jar", result.Content);
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

            var service = CreateLocalService(workspaceRoot, root);
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

    [Fact]
    public async Task TryOpenAsync_Ssh_ReadsViaClientWithoutLocalExists()
    {
        var remoteRoot = "/home/u/racing";
        var remoteFile = "/home/u/racing/server-report.html";
        var client = new InMemorySshClient(remoteRoot)
        {
            Files =
            {
                [remoteFile] = "<html>ok</html>"
            }
        };
        var service = CreateSshService(remoteRoot, client);

        var result = await service.TryOpenAsync(remoteFile);

        Assert.True(result.Succeeded);
        Assert.Equal("<html>ok</html>", result.Content);
        Assert.Equal(remoteFile, result.FullPath);
        Assert.DoesNotContain('\\', result.FullPath!);
    }

    [Fact]
    public async Task TryOpenAsync_Ssh_DoesNotRewriteUnixPathWithGetFullPath()
    {
        var remoteRoot = "/home/u/racing";
        var remoteFile = "/home/u/racing/readme.md";
        var client = new InMemorySshClient(remoteRoot)
        {
            Files = { [remoteFile] = "# hi" }
        };
        var service = CreateSshService(remoteRoot, client);

        var result = await service.TryOpenAsync("readme.md");

        Assert.True(result.Succeeded);
        Assert.Equal(remoteFile, result.FullPath);
        Assert.False(File.Exists(remoteFile));
    }

    [Fact]
    public async Task SaveAsync_Ssh_WritesViaClient()
    {
        var remoteRoot = "/home/u/racing";
        var remoteFile = "/home/u/racing/note.txt";
        var client = new InMemorySshClient(remoteRoot)
        {
            Files = { [remoteFile] = "v1" }
        };
        var service = CreateSshService(remoteRoot, client);

        var save = await service.SaveAsync(remoteFile, "v2");

        Assert.True(save.Succeeded);
        Assert.Equal("v2", client.Files[remoteFile]);
    }

    [Fact]
    public async Task TryOpenAsync_Ssh_FailsWhenDisconnected()
    {
        var service = CreateSshService("/home/u/racing", new InMemorySshClient("/home/u/racing") { IsConnectedOverride = false });
        var result = await service.TryOpenAsync("/home/u/racing/a.txt");

        Assert.False(result.Succeeded);
        Assert.Contains("SSH", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), $"athlon-editor-{Guid.NewGuid():N}");

    private static WorkspaceFileEditorService CreateLocalService(string workspaceRoot, string appDataRoot)
    {
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(workspaceRoot);
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), new AppSettings(), new TestPathProvider(appDataRoot));
        return new WorkspaceFileEditorService(guard, new AppSettings(), new InMemorySshClient(workspaceRoot) { IsConnectedOverride = false });
    }

    private static WorkspaceFileEditorService CreateSshService(string remoteRoot, ISshWorkspaceClient client)
    {
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(remoteRoot, WorkspaceKind.Ssh, "ws-ssh", "racing");
        var appDataRoot = Path.Combine(Path.GetTempPath(), $".athlon-agent-editor-ssh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDataRoot);
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), new AppSettings(), new TestPathProvider(appDataRoot));
        return new WorkspaceFileEditorService(guard, new AppSettings(), client);
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

    private sealed class InMemorySshClient(string remoteRoot) : ISshWorkspaceClient
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public bool IsConnectedOverride { get; set; } = true;

        public bool IsConnected => IsConnectedOverride;
        public string? RemoteRoot => remoteRoot;
        public string? ConnectedWorkspaceId => "ws-ssh";

        public Task ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files.ContainsKey(remotePath));

        public Task<SshFileInfo> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            if (!Files.TryGetValue(remotePath, out var content))
            {
                throw new FileNotFoundException(remotePath);
            }

            return Task.FromResult(new SshFileInfo(remotePath, content.Length, false, DateTimeOffset.UtcNow));
        }

        public Task<SshFileInfo?> TryGetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            if (!Files.TryGetValue(remotePath, out var content))
            {
                return Task.FromResult<SshFileInfo?>(null);
            }

            return Task.FromResult<SshFileInfo?>(new SshFileInfo(remotePath, Encoding.UTF8.GetByteCount(content), false, DateTimeOffset.UtcNow));
        }

        public Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files[remotePath]);

        public async Task<T> ReadViaStreamAsync<T>(
            string remotePath,
            Func<Stream, CancellationToken, Task<T>> reader,
            CancellationToken cancellationToken = default)
        {
            var bytes = Encoding.UTF8.GetBytes(Files[remotePath]);
            await using var stream = new MemoryStream(bytes);
            return await reader(stream, cancellationToken).ConfigureAwait(false);
        }

        public Task WriteTextAsync(string remotePath, string content, CancellationToken cancellationToken = default)
        {
            Files[remotePath] = content;
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<SshEntry> ListAsync(
            string remotePath,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public Task<SshCommandResult> ExecuteAsync(
            string command,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SshCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));

        public Task<bool> HasCommandAsync(string commandName, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
