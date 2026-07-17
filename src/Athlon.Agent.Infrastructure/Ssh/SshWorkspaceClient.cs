using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Athlon.Agent.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshWorkspaceClient(IAppLogger logger) : ISshWorkspaceClient, IDisposable
{
    private readonly IAppLogger _logger = logger.ForContext("SshWorkspaceClient");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _commandCache = new(StringComparer.Ordinal);
    private SshClient? _ssh;
    private SftpClient? _sftp;
    private string? _connectionKey;

    public bool IsConnected =>
        _ssh is { IsConnected: true } && _sftp is { IsConnected: true };

    public string? RemoteRoot { get; private set; }

    public string? ConnectedWorkspaceId { get; private set; }

    public async Task ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw new ArgumentException("SSH host is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            throw new ArgumentException("SSH username is required.", nameof(request));
        }

        var remoteRoot = RemotePathNormalizer.NormalizeRoot(request.RemoteRoot);
        if (string.IsNullOrWhiteSpace(remoteRoot))
        {
            remoteRoot = "/";
        }

        var key = $"{request.WorkspaceId}|{request.Host}|{request.Port}|{request.Username}|{remoteRoot}|{request.AuthMode}|{request.PrivateKeyPath}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected && string.Equals(_connectionKey, key, StringComparison.Ordinal))
            {
                RemoteRoot = remoteRoot;
                ConnectedWorkspaceId = request.WorkspaceId;
                return;
            }

            await DisconnectCoreAsync().ConfigureAwait(false);

            var authMethods = BuildAuthMethods(request);
            var connection = new ConnectionInfo(request.Host, request.Port <= 0 ? 22 : request.Port, request.Username, authMethods)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            var ssh = new SshClient(connection);
            var sftp = new SftpClient(connection);
            ssh.KeepAliveInterval = TimeSpan.FromSeconds(30);
            sftp.KeepAliveInterval = TimeSpan.FromSeconds(30);
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ssh.Connect();
                    cancellationToken.ThrowIfCancellationRequested();
                    sftp.Connect();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                SafeDispose(ssh);
                SafeDispose(sftp);
                throw;
            }

            _ssh = ssh;
            _sftp = sftp;
            _connectionKey = key;
            _commandCache.Clear();
            RemoteRoot = remoteRoot;
            ConnectedWorkspaceId = request.WorkspaceId;
            _logger.Information(
                "SSH connected host={Host} port={Port} user={User} root={Root}",
                request.Host,
                request.Port,
                request.Username,
                remoteRoot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var info = await TryGetFileInfoAsync(remotePath, cancellationToken).ConfigureAwait(false);
        return info is not null;
    }

    public async Task<SshFileInfo> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var info = await TryGetFileInfoAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            throw new FileNotFoundException($"Remote path not found: {remotePath}");
        }

        return info;
    }

    public async Task<SshFileInfo?> TryGetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var path = RemotePathNormalizer.Collapse(remotePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sftp = RequireSftp();
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!sftp.Exists(path))
                    {
                        return null;
                    }

                    var attrs = sftp.GetAttributes(path);
                    return ToFileInfo(path, attrs);
                }
                catch (SftpPathNotFoundException)
                {
                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        return await ReadViaStreamAsync(
                remotePath,
                async (stream, ct) =>
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                    return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<T> ReadViaStreamAsync<T>(
        string remotePath,
        Func<Stream, CancellationToken, Task<T>> reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var path = RemotePathNormalizer.Collapse(remotePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sftp = RequireSftp();
            await using var stream = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (Stream)sftp.OpenRead(path);
            }, cancellationToken).ConfigureAwait(false);
            return await reader(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteTextAsync(string remotePath, string content, CancellationToken cancellationToken = default)
    {
        var path = RemotePathNormalizer.Collapse(remotePath);
        var directory = RemotePathNormalizer.GetDirectoryName(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sftp = RequireSftp();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(directory) && directory != "/" && !sftp.Exists(directory))
                {
                    CreateDirectoryRecursive(sftp, directory);
                }

                using var stream = sftp.OpenWrite(path);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content ?? string.Empty);
                writer.Flush();
                stream.SetLength(stream.Position);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var path = RemotePathNormalizer.Collapse(remotePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sftp = RequireSftp();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CreateDirectoryRecursive(sftp, path);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<SshEntry> ListAsync(
        string remotePath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = RemotePathNormalizer.Collapse(remotePath);
        IList<ISftpFile> entries;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sftp = RequireSftp();
            entries = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (IList<ISftpFile>)sftp.ListDirectory(path).ToList();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var fullPath = RemotePathNormalizer.Combine(path, entry.Name);
            yield return new SshEntry(entry.Name, fullPath, entry.IsDirectory, entry.Length);
        }
    }

    public async Task<SshCommandResult> ExecuteAsync(
        string command,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var cwd = string.IsNullOrWhiteSpace(workingDirectory)
            ? RemoteRoot
            : RemotePathNormalizer.Collapse(workingDirectory);
        var wrapped = string.IsNullOrWhiteSpace(cwd)
            ? command
            : $"cd {ShellQuote(cwd)} && {command}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ssh = RequireSsh();
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                using var cmd = ssh.CreateCommand(wrapped);
                var asyncResult = cmd.BeginExecute();
                using var registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        cmd.CancelAsync();
                    }
                    catch
                    {
                        // ignore
                    }
                });

                if (!asyncResult.AsyncWaitHandle.WaitOne(timeout))
                {
                    try
                    {
                        cmd.CancelAsync();
                    }
                    catch
                    {
                        // ignore
                    }

                    throw new TimeoutException($"SSH command timed out after {timeout.TotalSeconds:0}s.");
                }

                var stdout = cmd.EndExecute(asyncResult) ?? string.Empty;
                sw.Stop();
                return new SshCommandResult(cmd.ExitStatus ?? -1, stdout, cmd.Error ?? string.Empty, sw.Elapsed);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasCommandAsync(string commandName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return false;
        }

        var name = commandName.Trim();
        if (_commandCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var result = await ExecuteAsync(
                $"command -v {ShellQuote(name)} >/dev/null 2>&1",
                RemoteRoot,
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);
        var exists = result.ExitCode == 0;
        _commandCache[name] = exists;
        return exists;
    }

    public void Dispose()
    {
        DisconnectCoreAsync().GetAwaiter().GetResult();
        _gate.Dispose();
    }

    private async Task DisconnectCoreAsync()
    {
        var ssh = _ssh;
        var sftp = _sftp;
        _ssh = null;
        _sftp = null;
        _connectionKey = null;
        _commandCache.Clear();
        RemoteRoot = null;
        ConnectedWorkspaceId = null;

        await Task.Run(() =>
        {
            try
            {
                if (sftp is { IsConnected: true })
                {
                    sftp.Disconnect();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to disconnect SFTP client: {Message}", ex.Message);
            }

            try
            {
                if (ssh is { IsConnected: true })
                {
                    ssh.Disconnect();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to disconnect SSH client: {Message}", ex.Message);
            }

            SafeDispose(sftp);
            SafeDispose(ssh);
        }).ConfigureAwait(false);
    }

    private SftpClient RequireSftp()
    {
        if (_sftp is not { IsConnected: true })
        {
            throw new InvalidOperationException("SSH not connected");
        }

        return _sftp;
    }

    private SshClient RequireSsh()
    {
        if (_ssh is not { IsConnected: true })
        {
            throw new InvalidOperationException("SSH not connected");
        }

        return _ssh;
    }

    private static SshFileInfo ToFileInfo(string path, SftpFileAttributes attrs) =>
        new(
            path,
            attrs.Size,
            attrs.IsDirectory,
            attrs.LastWriteTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(attrs.LastWriteTime, DateTimeKind.Utc)
                : attrs.LastWriteTime.ToUniversalTime());

    private static AuthenticationMethod[] BuildAuthMethods(SshConnectRequest request)
    {
        if (string.Equals(request.AuthMode, "privateKey", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.PrivateKeyPath) || !File.Exists(request.PrivateKeyPath))
            {
                throw new InvalidOperationException("Private key file was not found.");
            }

            PrivateKeyFile keyFile = string.IsNullOrEmpty(request.PrivateKeyPassphrase)
                ? new PrivateKeyFile(request.PrivateKeyPath)
                : new PrivateKeyFile(request.PrivateKeyPath, request.PrivateKeyPassphrase);
            return [new PrivateKeyAuthenticationMethod(request.Username, keyFile)];
        }

        return [new PasswordAuthenticationMethod(request.Username, request.Password ?? string.Empty)];
    }

    private static void CreateDirectoryRecursive(SftpClient sftp, string path)
    {
        var normalized = RemotePathNormalizer.Collapse(path);
        if (normalized is "/" or "" || sftp.Exists(normalized))
        {
            return;
        }

        var parent = RemotePathNormalizer.GetDirectoryName(normalized);
        if (!string.IsNullOrWhiteSpace(parent) && parent != normalized)
        {
            CreateDirectoryRecursive(sftp, parent);
        }

        sftp.CreateDirectory(normalized);
    }

    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void SafeDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
