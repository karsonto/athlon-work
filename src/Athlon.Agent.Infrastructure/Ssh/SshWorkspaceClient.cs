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
/// Per-root-session SSH/SFTP connection pool. Each root session keeps its own independent
/// connection slot, so switching the displayed session or running turns in several sessions
/// concurrently never tears down another session's in-use connection. Slots idle for
/// <see cref="IdleReclaimInterval"/> are disconnected and removed by a background reaper.
/// </summary>
public sealed class SshWorkspaceClient(IAppLogger logger)
    : ISshWorkspaceClient, ISshConnectionRegistry, IDisposable
{
    internal static readonly TimeSpan IdleReclaimInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReaperPeriod = TimeSpan.FromMinutes(1);

    private readonly IAppLogger _logger = logger.ForContext("SshWorkspaceClient");
    private readonly ConcurrentDictionary<string, SshConnectionSlot> _slots = new(StringComparer.Ordinal);
    private volatile string? _defaultSessionId;
    private Timer? _reaper;
    private int _reaperStarted;

    /// <inheritdoc />
    public string? DefaultSessionId
    {
        get => _defaultSessionId;
        set => _defaultSessionId = value;
    }

    /// <summary>
    /// True when the connection slot for the current context (turn root session, falling back
    /// to <see cref="DefaultSessionId"/>) is connected.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            var sessionId = ResolveSessionId();
            return sessionId is not null
                && _slots.TryGetValue(sessionId, out var slot)
                && slot.IsConnected;
        }
    }

    // ---- ISshConnectionRegistry ----

    /// <inheritdoc />
    public async Task EnsureConnectedAsync(
        string rootSessionId,
        SshConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        EnsureReaperStarted();

        // The idle reaper may remove a slot while we connect it; retry so the caller always
        // leaves this method with its session's slot present and connected.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var slot = _slots.GetOrAdd(rootSessionId, static (_, logger) => new SshConnectionSlot(logger), _logger);
            await slot.ConnectAsync(request, cancellationToken).ConfigureAwait(false);

            if (_slots.TryGetValue(rootSessionId, out var current) && ReferenceEquals(current, slot))
            {
                return;
            }

            // The slot was reaped while we connected it. Re-adopt it, or fall back to the
            // slot another caller registered for the same session.
            if (_slots.TryAdd(rootSessionId, slot))
            {
                return;
            }

            await slot.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(string rootSessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootSessionId))
        {
            return;
        }

        if (_slots.TryRemove(rootSessionId, out var slot))
        {
            await slot.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        var slots = _slots.Values.Distinct().ToArray();
        _slots.Clear();
        await Task.WhenAll(slots.Select(slot => slot.DisconnectAsync(cancellationToken))).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool IsSessionConnected(string rootSessionId) =>
        !string.IsNullOrWhiteSpace(rootSessionId)
        && _slots.TryGetValue(rootSessionId, out var slot)
        && slot.IsConnected;

    // ---- ISshWorkspaceClient (routed operations) ----

    public async Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        var result = await slot.FileExistsAsync(remotePath, cancellationToken).ConfigureAwait(false);
        slot.Touch();
        return result;
    }

    public async Task<SshFileInfo> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        var result = await slot.GetFileInfoAsync(remotePath, cancellationToken).ConfigureAwait(false);
        slot.Touch();
        return result;
    }

    public async Task<SshFileInfo?> TryGetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        var result = await slot.TryGetFileInfoAsync(remotePath, cancellationToken).ConfigureAwait(false);
        slot.Touch();
        return result;
    }

    public async Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        var result = await slot.ReadTextAsync(remotePath, cancellationToken).ConfigureAwait(false);
        slot.Touch();
        return result;
    }

    public async Task<T> ReadViaStreamAsync<T>(
        string remotePath,
        Func<Stream, CancellationToken, Task<T>> reader,
        CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        var result = await slot.ReadViaStreamAsync(remotePath, reader, cancellationToken).ConfigureAwait(false);
        slot.Touch();
        return result;
    }

    public async Task WriteTextAsync(string remotePath, string content, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        await slot.WriteTextAsync(remotePath, content, cancellationToken).ConfigureAwait(false);
        slot.Touch();
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        await slot.DownloadFileAsync(remotePath, localPath, cancellationToken).ConfigureAwait(false);
        slot.Touch();
    }

    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        await slot.UploadFileAsync(localPath, remotePath, cancellationToken).ConfigureAwait(false);
        slot.Touch();
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        await slot.CreateDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false);
        slot.Touch();
    }

    public async IAsyncEnumerable<SshEntry> ListAsync(
        string remotePath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        await foreach (var entry in slot.ListAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }

        slot.Touch();
    }

    public async Task<SshCommandResult> ExecuteAsync(
        string command,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        var result = await slot.ExecuteAsync(command, workingDirectory, timeout, cancellationToken).ConfigureAwait(false);
        slot.Touch();
        return result;
    }

    public async Task<bool> HasCommandAsync(string commandName, CancellationToken cancellationToken = default)
    {
        var slot = ResolveConnectedSlot();
        var result = await slot.HasCommandAsync(commandName, cancellationToken).ConfigureAwait(false);
        slot.Touch();
        return result;
    }

    public void Dispose()
    {
        try
        {
            _reaper?.Dispose();
        }
        catch
        {
            // ignore
        }

        _reaper = null;
        var slots = _slots.Values.Distinct().ToArray();
        _slots.Clear();
        foreach (var slot in slots)
        {
            try
            {
                slot.Dispose();
            }
            catch
            {
                // Keep disposing the remaining slots.
            }
        }
    }

    // ---- Routing & reaper ----

    private string? ResolveSessionId()
    {
        var scoped = SshRootSessionScope.CurrentSessionId;
        if (!string.IsNullOrEmpty(scoped))
        {
            return scoped;
        }

        return _defaultSessionId;
    }

    private SshConnectionSlot ResolveConnectedSlot()
    {
        var sessionId = ResolveSessionId();
        if (sessionId is not null
            && _slots.TryGetValue(sessionId, out var slot)
            && slot.IsConnected)
        {
            return slot;
        }

        throw new InvalidOperationException("SSH not connected");
    }

    private void EnsureReaperStarted()
    {
        if (Volatile.Read(ref _reaperStarted) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _reaperStarted, 1, 0) != 0)
        {
            return;
        }

        _reaper = new Timer(
            static state => ((SshWorkspaceClient)state!).ReapIdleSlots(),
            this,
            ReaperPeriod,
            ReaperPeriod);
    }

    private async void ReapIdleSlots()
    {
        try
        {
            var cutoff = DateTime.UtcNow - IdleReclaimInterval;
            foreach (var pair in _slots)
            {
                var slot = pair.Value;
                if (slot.LastUsedUtc > cutoff)
                {
                    continue;
                }

                // Never reap a slot while a command is executing on it.
                if (!slot._gate.Wait(0))
                {
                    continue;
                }

                try
                {
                    // Remove before disconnecting so a concurrent EnsureConnectedAsync creates a
                    // fresh slot instead of waiting on (and then reconnecting) a reaped one.
                    if (!_slots.TryRemove(new KeyValuePair<string, SshConnectionSlot>(pair.Key, slot)))
                    {
                        // A newer slot replaced this one; leave it alone.
                        continue;
                    }

                    _logger.Information(
                        "SSH connection idle-reclaimed session={SessionId} idleSince={IdleSince:O}",
                        pair.Key,
                        slot.LastUsedUtc);
                    // The gate is already held (see slot._gate.Wait above), so run the core
                    // disconnect directly instead of re-acquiring the gate.
                    await slot.DisconnectCoreAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warning("SSH idle reaper failed for session {SessionId}: {Message}", pair.Key, ex.Message);
                }
                finally
                {
                    slot._gate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("SSH idle reaper scan failed: {Message}", ex.Message);
        }
    }

    /// <summary>Shell-escapes a single argument for a remote POSIX command line.</summary>
    internal static string ShellQuote(string value) => SshConnectionSlot.ShellQuote(value);

    /// <summary>
    /// A single live SSH/SFTP connection owned by one root session. All remote operations are
    /// serialized on <see cref="_gate"/> (per slot), matching the original single-connection client.
    /// </summary>
    private sealed class SshConnectionSlot(IAppLogger logger) : IDisposable
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
}
