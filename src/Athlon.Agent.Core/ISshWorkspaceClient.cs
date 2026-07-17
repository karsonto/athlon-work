namespace Athlon.Agent.Core;

public sealed record SshConnectRequest(
    string WorkspaceId,
    string Host,
    int Port,
    string Username,
    string RemoteRoot,
    string AuthMode,
    string? PrivateKeyPath,
    string? Password,
    string? PrivateKeyPassphrase);

public sealed record SshFileInfo(string Path, long Length, bool IsDirectory, DateTimeOffset? LastWriteTimeUtc);

public sealed record SshEntry(string Name, string FullPath, bool IsDirectory, long Length);

public sealed record SshCommandResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration);

public interface ISshWorkspaceClient
{
    bool IsConnected { get; }

    string? RemoteRoot { get; }

    string? ConnectedWorkspaceId { get; }

    Task ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default);

    Task<SshFileInfo> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the path does not exist (single round-trip).</summary>
    Task<SshFileInfo?> TryGetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default);

    Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a remote file for sequential reading under the client lock.
    /// Prefer this over <see cref="ReadTextAsync"/> when only a line range is needed.
    /// </summary>
    Task<T> ReadViaStreamAsync<T>(
        string remotePath,
        Func<Stream, CancellationToken, Task<T>> reader,
        CancellationToken cancellationToken = default);

    Task WriteTextAsync(string remotePath, string content, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default);

    IAsyncEnumerable<SshEntry> ListAsync(string remotePath, CancellationToken cancellationToken = default);

    Task<SshCommandResult> ExecuteAsync(
        string command,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Cached probe for a remote executable (e.g. rg, find).</summary>
    Task<bool> HasCommandAsync(string commandName, CancellationToken cancellationToken = default);
}
