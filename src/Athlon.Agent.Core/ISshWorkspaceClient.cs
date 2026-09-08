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

/// <summary>
/// SSH/SFTP file operations routed to the connection slot of the current context:
/// an in-flight agent turn resolves its root session (see <c>SshRootSessionScope</c>),
/// and non-turn UI callers resolve the currently displayed session
/// (<see cref="ISshConnectionRegistry.DefaultSessionId"/>).
/// Connection lifecycle (connect/disconnect/idle reclamation) lives on
/// <see cref="ISshConnectionRegistry"/> and is keyed per root session.
/// </summary>
public interface ISshWorkspaceClient
{
    /// <summary>
    /// True when the connection slot for the current context (turn root session, falling
    /// back to the displayed session) is connected.
    /// </summary>
    bool IsConnected { get; }

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

    /// <summary>Download a remote file to a local path (binary-safe).</summary>
    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);

    /// <summary>Upload a local file to a remote path (binary-safe). Creates parent directories as needed.</summary>
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);

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
