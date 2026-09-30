using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Athlon.Agent.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Athlon.Agent.Infrastructure.Ssh;

/// <summary>
/// A single live SSH/SFTP connection owned by one root session. All remote operations are
/// serialized on <see cref="_gate"/> (per slot), matching the original single-connection client.
/// </summary>
internal sealed class SshConnectionSlot(IAppLogger logger) : IDisposable
{
    private readonly IAppLogger _logger = logger;
    internal readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _commandCache = new(StringComparer.Ordinal);
    private SshClient? _ssh;
    private SftpClient? _sftp;
    private string? _connectionKey;
    private long _lastUsedTicks = DateTime.UtcNow.Ticks;

    public bool IsConnected =>
        _ssh is { IsConnected: true } && _sftp is { IsConnected: true };

    public string? RemoteRoot { get; private set; }

    public string? ConnectedWorkspaceId { get; private set; }

    public DateTime LastUsedUtc
    {
        get => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
    }

    public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

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
                Touch();
                return;
            }

            await DisconnectCoreAsync().ConfigureAwait(false);

            var (ssh, sftp) = SshConnectionFactory.CreateClients(request);
            try
            {
                await Task.Run(
                    () => SshConnectionFactory.ConnectPair(ssh, sftp, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                SshConnectionFactory.SafeDispose(ssh, sftp);
                throw;
            }

            _ssh = ssh;
            _sftp = sftp;
            _connectionKey = key;
            _commandCache.Clear();
            RemoteRoot = remoteRoot;
            ConnectedWorkspaceId = request.WorkspaceId;
            Touch();
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

    public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var path = RemotePathNormalizer.Collapse(remotePath);
        var localFullPath = Path.GetFullPath(localPath);
        var localDirectory = Path.GetDirectoryName(localFullPath);
        if (!string.IsNullOrWhiteSpace(localDirectory))
        {
            Directory.CreateDirectory(localDirectory);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sftp = RequireSftp();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var remote = sftp.OpenRead(path);
                using var local = File.Create(localFullPath);
                remote.CopyTo(local);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var localFullPath = Path.GetFullPath(localPath);
        if (!File.Exists(localFullPath))
        {
            throw new FileNotFoundException("Local file not found.", localFullPath);
        }

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

                using var local = File.OpenRead(localFullPath);
                using var remote = sftp.OpenWrite(path);
                local.CopyTo(remote);
                remote.Flush();
                remote.SetLength(remote.Position);
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
            // Do not pass cancellationToken into Task.Run: SSH.NET signals cancel via CancelAsync,
            // and EndExecute then throws TaskCanceledException which we map explicitly below.
            return await Task.Run(
                    () => ExecuteCommandCore(ssh, wrapped, timeout, cancellationToken),
                    CancellationToken.None)
                .ConfigureAwait(false);
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

        try
        {
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning("Remote command probe failed for {Command}: {Message}", name, ex.Message);
            _commandCache[name] = false;
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _gate.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Fall through to best-effort disconnect below.
        }

        try
        {
            DisconnectCoreSync();
        }
        catch
        {
            // Best-effort disconnect.
        }

        try
        {
            _gate.Release();
        }
        catch
        {
            // Gate may not have been acquired.
        }

        try
        {
            _gate.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    internal async Task DisconnectCoreAsync()
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

            SshConnectionFactory.SafeDispose(sftp, ssh);
        }).ConfigureAwait(false);
    }

    private void DisconnectCoreSync()
    {
        var ssh = _ssh;
        var sftp = _sftp;
        _ssh = null;
        _sftp = null;
        _connectionKey = null;
        _commandCache.Clear();
        RemoteRoot = null;
        ConnectedWorkspaceId = null;

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

        SshConnectionFactory.SafeDispose(sftp, ssh);
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

    private static SshCommandResult ExecuteCommandCore(
        SshClient ssh,
        string wrappedCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        using var cmd = ssh.CreateCommand(wrappedCommand);
        var asyncResult = cmd.BeginExecute();
        using var registration = cancellationToken.Register(static state =>
        {
            try
            {
                ((SshCommand)state!).CancelAsync();
            }
            catch
            {
                // ignore cancel races while disposing/disconnecting
            }
        }, cmd);

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

            TryEndExecuteQuietly(cmd, asyncResult);
            throw new TimeoutException($"SSH command timed out after {timeout.TotalSeconds:0}s.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            TryEndExecuteQuietly(cmd, asyncResult);
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            var stdout = cmd.EndExecute(asyncResult) ?? string.Empty;
            sw.Stop();
            return new SshCommandResult(cmd.ExitStatus ?? -1, stdout, cmd.Error ?? string.Empty, sw.Elapsed);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("SSH command was canceled.", ex, cancellationToken);
            }

            // SSH.NET aborted the command without our token (disconnect / library cancel).
            throw new InvalidOperationException("SSH command was aborted.", ex);
        }
    }

    private static void TryEndExecuteQuietly(SshCommand cmd, IAsyncResult asyncResult)
    {
        try
        {
            _ = cmd.EndExecute(asyncResult);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or ObjectDisposedException)
        {
            // Expected after CancelAsync / timeout.
        }
        catch
        {
            // Best-effort drain; ignore secondary failures.
        }
    }

    private static SshFileInfo ToFileInfo(string path, SftpFileAttributes attrs) =>
        new(
            path,
            attrs.Size,
            attrs.IsDirectory,
            attrs.LastWriteTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(attrs.LastWriteTime, DateTimeKind.Utc)
                : attrs.LastWriteTime.ToUniversalTime());

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
}
