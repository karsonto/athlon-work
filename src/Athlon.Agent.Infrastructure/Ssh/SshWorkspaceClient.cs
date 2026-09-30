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

}
