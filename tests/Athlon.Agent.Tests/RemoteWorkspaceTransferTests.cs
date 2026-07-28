using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class RemoteWorkspaceTransferHelperTests
{
    [Theory]
    [InlineData("/home/ws", null, false, "/home/ws")]
    [InlineData("/home/ws", "/home/ws/src", true, "/home/ws/src")]
    [InlineData("/home/ws", "/home/ws/src/a.txt", false, "/home/ws/src")]
    [InlineData("/home/ws", "/home/ws/readme.md", false, "/home/ws")]
    public void ResolveDropTargetDirectory_ReturnsExpected(
        string workspaceRoot,
        string? hitPath,
        bool isDirectory,
        string expected)
    {
        var actual = RemoteWorkspaceTransferHelper.ResolveDropTargetDirectory(workspaceRoot, hitPath, isDirectory);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsRemoteTargetAllowed_RejectsOutsideWorkspace()
    {
        Assert.True(RemoteWorkspaceTransferHelper.IsRemoteTargetAllowed("/home/ws", "/home/ws/src"));
        Assert.False(RemoteWorkspaceTransferHelper.IsRemoteTargetAllowed("/home/ws", "/tmp"));
    }

    [Theory]
    [InlineData("/home/ws", "/home/ws/a.txt", "a.txt")]
    [InlineData("/home/ws", "/home/ws/src/a.txt", "src/a.txt")]
    [InlineData("/", "/etc/hosts", "etc/hosts")]
    public void ToRelativeRemotePath_StripsRoot(string root, string file, string expected)
    {
        Assert.Equal(expected, RemoteWorkspaceTransferHelper.ToRelativeRemotePath(root, file));
    }
}

public sealed class SshWorkspaceTransferTests
{
    [Fact]
    public async Task InMemorySshClient_DownloadAndUpload_RoundTrip()
    {
        var client = new TransferSshClient("/workspace");
        client.Files["/workspace/hello.txt"] = "hello-remote";

        using var temp = new TempDirectoryScope("athlon-ssh-transfer");
        var localPath = Path.Combine(temp.Root, "hello.txt");

        await client.DownloadFileAsync("/workspace/hello.txt", localPath);
        Assert.Equal("hello-remote", await File.ReadAllTextAsync(localPath));

        await File.WriteAllTextAsync(localPath, "hello-local");
        await client.UploadFileAsync(localPath, "/workspace/uploaded.txt");
        Assert.Equal("hello-local", client.Files["/workspace/uploaded.txt"]);
    }

    [Fact]
    public async Task InMemorySshClient_ListAsync_ReturnsChildren()
    {
        var client = new TransferSshClient("/workspace");
        client.Files["/workspace/a.txt"] = "a";
        client.Files["/workspace/src/b.txt"] = "b";

        var entries = new List<SshEntry>();
        await foreach (var entry in client.ListAsync("/workspace"))
        {
            entries.Add(entry);
        }

        Assert.Contains(entries, e => e.Name == "a.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "src" && e.IsDirectory);
    }

    private sealed class TransferSshClient(string remoteRoot) : ISshWorkspaceClient
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public bool IsConnected => true;
        public string? RemoteRoot => remoteRoot;
        public string? ConnectedWorkspaceId => "ws";

        public Task ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files.ContainsKey(RemotePathNormalizer.Collapse(remotePath)));
        public Task<SshFileInfo> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SshFileInfo?> TryGetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            var path = RemotePathNormalizer.Collapse(remotePath);
            return Files.TryGetValue(path, out var content)
                ? Task.FromResult<SshFileInfo?>(new SshFileInfo(path, content.Length, false, DateTimeOffset.UtcNow))
                : Task.FromResult<SshFileInfo?>(null);
        }

        public Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files[RemotePathNormalizer.Collapse(remotePath)]);

        public Task<T> ReadViaStreamAsync<T>(
            string remotePath,
            Func<Stream, CancellationToken, Task<T>> reader,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteTextAsync(string remotePath, string content, CancellationToken cancellationToken = default)
        {
            Files[RemotePathNormalizer.Collapse(remotePath)] = content;
            return Task.CompletedTask;
        }

        public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
        {
            var content = Files[RemotePathNormalizer.Collapse(remotePath)];
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(localPath, content, cancellationToken).ConfigureAwait(false);
        }

        public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
        {
            Files[RemotePathNormalizer.Collapse(remotePath)] =
                await File.ReadAllTextAsync(localPath, cancellationToken).ConfigureAwait(false);
        }

        public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<SshEntry> ListAsync(
            string remotePath,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            var root = RemotePathNormalizer.Collapse(remotePath).TrimEnd('/');
            if (root.Length == 0)
            {
                root = "/";
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in Files.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var collapsed = RemotePathNormalizer.Collapse(path);
                string relative;
                if (root == "/")
                {
                    relative = collapsed.TrimStart('/');
                }
                else if (collapsed.StartsWith(root + "/", StringComparison.Ordinal))
                {
                    relative = collapsed[(root.Length + 1)..];
                }
                else
                {
                    continue;
                }

                var slash = relative.IndexOf('/');
                var name = slash < 0 ? relative : relative[..slash];
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                {
                    continue;
                }

                var fullPath = root == "/" ? "/" + name : root + "/" + name;
                var isDir = slash >= 0;
                var length = isDir ? 0 : Files[path].Length;
                yield return new SshEntry(name, fullPath, isDir, length);
            }
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
