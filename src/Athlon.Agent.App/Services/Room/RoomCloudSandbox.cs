using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using Athlon.Agent.CloudSandbox.V1;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.CloudSandbox;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Cloud sandbox backend over gRPC + AES-256-GCM sealed envelopes.</summary>
public sealed class RoomCloudSandbox : IRoomSandboxBackend, IAsyncDisposable
{
    private const int StreamChunk = 64 * 1024;

    private readonly AppSettings _settings;
    private readonly ILogger<RoomCloudSandbox> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CloudSessionState> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private GrpcChannel? _channel;
    private global::Athlon.Agent.CloudSandbox.V1.CloudSandbox.CloudSandboxClient? _client;
    private byte[]? _sharedKey;

    public RoomCloudSandbox(AppSettings settings, ILogger<RoomCloudSandbox> logger)
    {
        _settings = settings;
        _logger = logger;
        // Allow h2c (HTTP/2 without TLS) for intranet endpoints.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    public async Task<RoomSandboxProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var cloud = _settings.CloudSandbox;
        if (string.IsNullOrWhiteSpace(cloud.BaseUrl)
            || !Uri.TryCreate(cloud.BaseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            return new RoomSandboxProbeResult
            {
                Available = false,
                ImageReady = false,
                Message = "未配置云沙箱服务（CloudSandbox:BaseUrl）。"
            };
        }

        if (string.IsNullOrWhiteSpace(TryGetApiKey()))
        {
            return new RoomSandboxProbeResult
            {
                Available = false,
                ImageReady = false,
                Message = "云沙箱需要配置 CloudSandbox:ApiKey。"
            };
        }

        if (string.IsNullOrWhiteSpace(cloud.SharedKey))
        {
            return new RoomSandboxProbeResult
            {
                Available = false,
                ImageReady = false,
                Message = "云沙箱需要配置 CloudSandbox:SharedKey（Base64 AES-256）。"
            };
        }

        try
        {
            EnsureClient();
            var key = RequireKey();
            var env = CloudSandboxSecure.Seal(key, new StatusRequest(), CloudSandboxRpcs.Status);
            var reply = await _client!.StatusAsync(env, BuildHeaders(), cancellationToken: WithTimeout(cancellationToken))
                .ConfigureAwait(false);
            var status = CloudSandboxSecure.Open(
                key, reply, StatusResponse.Parser, CloudSandboxRpcs.Status);

            if (!status.Available)
            {
                return new RoomSandboxProbeResult
                {
                    Available = false,
                    ImageReady = false,
                    Message = string.IsNullOrWhiteSpace(status.Message) ? "云沙箱当前不可用。" : status.Message,
                    QuotaHint = status.QuotaHint
                };
            }

            return new RoomSandboxProbeResult
            {
                Available = true,
                ImageReady = true,
                Message = string.IsNullOrWhiteSpace(status.Message)
                    ? "云沙箱可用；工作区将同步至 Athlon 云端。"
                    : status.Message,
                QuotaHint = status.QuotaHint,
                RuntimeHints = status.RuntimeHints.Count > 0
                    ? status.RuntimeHints.ToArray()
                    : ["python3.12", "java8", "node24", "chromium"]
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cloud sandbox probe failed.");
            return new RoomSandboxProbeResult
            {
                Available = false,
                ImageReady = false,
                Message = $"无法连接云沙箱：{ex.Message}"
            };
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.Available)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(probe.Message) ? "云沙箱不可用。" : probe.Message);
        }
    }

    public async Task EnsureRoomSessionAsync(
        string roomId,
        string hostWorkspaceRoot,
        string hostSkillsRoot,
        IReadOnlyList<string>? skillNames = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        var ws = RoomSandboxPathMap.NormalizeHostRoot(hostWorkspaceRoot)
            ?? throw new InvalidOperationException("云沙箱需要有效的房间工作区路径。");
        var skills = RoomSandboxPathMap.NormalizeHostRoot(hostSkillsRoot)
            ?? throw new InvalidOperationException("Skills 目录不可用。");

        if (!Directory.Exists(ws))
        {
            throw new InvalidOperationException($"工作区不存在: {ws}");
        }

        Directory.CreateDirectory(skills);
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(roomId, out var existing)
                && string.Equals(existing.WorkspaceRoot, ws, StringComparison.OrdinalIgnoreCase))
            {
                await PushSyncAsync(existing.SessionId, ws, skills, skillNames, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (_sessions.TryRemove(roomId, out var stale))
            {
                await DeleteSessionQuietAsync(stale.SessionId, cancellationToken).ConfigureAwait(false);
            }

            var sessionId = await CreateSessionAsync(roomId, cancellationToken).ConfigureAwait(false);
            await PushSyncAsync(sessionId, ws, skills, skillNames, cancellationToken).ConfigureAwait(false);
            _sessions[roomId] = new CloudSessionState(sessionId, ws, skills);
            _logger.LogInformation("Cloud sandbox session {SessionId} ready for room {RoomId}", sessionId, roomId);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task PullWorkspaceAsync(
        string roomId,
        string hostWorkspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(roomId, out var state))
        {
            return;
        }

        var ws = RoomSandboxPathMap.NormalizeHostRoot(hostWorkspaceRoot) ?? state.WorkspaceRoot;
        Directory.CreateDirectory(ws);

        EnsureClient();
        var key = RequireKey();
        var req = CloudSandboxSecure.Seal(
            key,
            new PullWorkspaceRequest { SessionId = state.SessionId },
            CloudSandboxRpcs.PullWorkspace,
            state.SessionId);

        await using var ms = new MemoryStream();
        using var call = _client!.PullWorkspace(
            req,
            BuildHeaders(state.SessionId),
            cancellationToken: WithTimeout(cancellationToken));
        await foreach (var env in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var chunk = CloudSandboxSecure.Open(
                key, env, PullWorkspaceChunk.Parser, CloudSandboxRpcs.PullWorkspace, state.SessionId);
            if (chunk.Empty)
            {
                return;
            }

            if (!chunk.Data.IsEmpty)
            {
                chunk.Data.WriteTo(ms);
            }
        }

        if (ms.Length == 0)
        {
            return;
        }

        ms.Position = 0;
        CloudWorkspaceSync.UnpackTarGzToDirectory(ms, ws);
    }

    public async Task DisposeRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return;
        }

        if (!_sessions.TryRemove(roomId, out var state))
        {
            return;
        }

        await DeleteSessionQuietAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomSandboxExecResult> ExecAsync(
        string roomId,
        string command,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var state = RequireSession(roomId);
        EnsureClient();
        var key = RequireKey();
        var req = CloudSandboxSecure.Seal(
            key,
            new ExecRequest
            {
                SessionId = state.SessionId,
                Command = command,
                Cwd = string.IsNullOrWhiteSpace(workingDirectory)
                    ? RoomSandboxPathMap.ContainerWorkspaceRoot
                    : workingDirectory,
                TimeoutSec = Math.Clamp(timeoutSeconds, 1, 600)
            },
            CloudSandboxRpcs.Exec,
            state.SessionId);

        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var exitCode = -1;
        using var call = _client!.Exec(
            req,
            BuildHeaders(state.SessionId),
            cancellationToken: WithTimeout(cancellationToken));
        await foreach (var env in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var chunk = CloudSandboxSecure.Open(
                key, env, ExecChunk.Parser, CloudSandboxRpcs.Exec, state.SessionId);
            if (!chunk.Stdout.IsEmpty)
            {
                chunk.Stdout.WriteTo(stdout);
            }

            if (!chunk.Stderr.IsEmpty)
            {
                chunk.Stderr.WriteTo(stderr);
            }

            if (chunk.HasExitCode)
            {
                exitCode = chunk.ExitCode;
            }
        }

        return new RoomSandboxExecResult
        {
            ExitCode = exitCode,
            Stdout = System.Text.Encoding.UTF8.GetString(stdout.ToArray()),
            Stderr = System.Text.Encoding.UTF8.GetString(stderr.ToArray())
        };
    }

    public async Task<byte[]?> ReadFileAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var state = RequireSession(roomId);
        EnsureClient();
        var key = RequireKey();
        var req = CloudSandboxSecure.Seal(
            key,
            new ReadFileRequest { SessionId = state.SessionId, Path = containerPath },
            CloudSandboxRpcs.ReadFile,
            state.SessionId);

        await using var ms = new MemoryStream();
        using var call = _client!.ReadFile(
            req,
            BuildHeaders(state.SessionId),
            cancellationToken: WithTimeout(cancellationToken));
        await foreach (var env in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var chunk = CloudSandboxSecure.Open(
                key, env, ReadFileChunk.Parser, CloudSandboxRpcs.ReadFile, state.SessionId);
            if (chunk.NotFound)
            {
                return null;
            }

            if (!chunk.Data.IsEmpty)
            {
                chunk.Data.WriteTo(ms);
            }
        }

        return ms.ToArray();
    }

    public async Task WriteFileAsync(
        string roomId,
        string containerPath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var state = RequireSession(roomId);
        if (containerPath.StartsWith(RoomSandboxPathMap.ContainerSkillsRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("云沙箱 Skills 目录为只读。");
        }

        EnsureClient();
        var key = RequireKey();
        using var call = _client!.WriteFile(
            BuildHeaders(state.SessionId),
            cancellationToken: WithTimeout(cancellationToken));

        await call.RequestStream.WriteAsync(
                CloudSandboxSecure.Seal(
                    key,
                    new WriteFileChunk
                    {
                        Header = new WriteFileHeader
                        {
                            SessionId = state.SessionId,
                            Path = containerPath
                        }
                    },
                    CloudSandboxRpcs.WriteFile,
                    state.SessionId),
                cancellationToken)
            .ConfigureAwait(false);

        content ??= Array.Empty<byte>();
        for (var i = 0; i < content.Length; i += StreamChunk)
        {
            var len = Math.Min(StreamChunk, content.Length - i);
            await call.RequestStream.WriteAsync(
                    CloudSandboxSecure.Seal(
                        key,
                        new WriteFileChunk
                        {
                            Data = ByteString.CopyFrom(content, i, len)
                        },
                        CloudSandboxRpcs.WriteFile,
                        state.SessionId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        _ = await call.ResponseAsync.ConfigureAwait(false);
    }

    public async Task EnsureDirectoryAsync(
        string roomId,
        string containerDirectory,
        CancellationToken cancellationToken = default)
    {
        var state = RequireSession(roomId);
        var dir = string.IsNullOrWhiteSpace(containerDirectory)
            ? RoomSandboxPathMap.ContainerWorkspaceRoot
            : containerDirectory.TrimEnd('/');
        EnsureClient();
        var key = RequireKey();
        var env = CloudSandboxSecure.Seal(
            key,
            new MkDirRequest { SessionId = state.SessionId, Path = dir },
            CloudSandboxRpcs.MkDir,
            state.SessionId);
        await _client!.MkDirAsync(env, BuildHeaders(state.SessionId), cancellationToken: WithTimeout(cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<bool> PathExistsAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var stat = await PathStatAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false);
        return stat.Exists;
    }

    public async Task<bool> IsDirectoryAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var stat = await PathStatAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false);
        return stat.IsDirectory;
    }

    public async Task<IReadOnlyList<string>> ListDirectoryAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var state = RequireSession(roomId);
        EnsureClient();
        var key = RequireKey();
        var env = CloudSandboxSecure.Seal(
            key,
            new ListDirRequest { SessionId = state.SessionId, Path = containerPath },
            CloudSandboxRpcs.ListDir,
            state.SessionId);
        var reply = await _client!.ListDirAsync(
                env, BuildHeaders(state.SessionId), cancellationToken: WithTimeout(cancellationToken))
            .ConfigureAwait(false);
        var parsed = CloudSandboxSecure.Open(
            key, reply, ListDirResponse.Parser, CloudSandboxRpcs.ListDir, state.SessionId);
        return parsed.Names.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var roomId in _sessions.Keys.ToArray())
        {
            try
            {
                await DisposeRoomAsync(roomId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cloud DisposeAsync failed for {RoomId}", roomId);
            }
        }

        _sessionGate.Dispose();
        _channel?.Dispose();
        _channel = null;
        _client = null;
    }

    private async Task<PathStatResponse> PathStatAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken)
    {
        var state = RequireSession(roomId);
        EnsureClient();
        var key = RequireKey();
        var env = CloudSandboxSecure.Seal(
            key,
            new PathStatRequest { SessionId = state.SessionId, Path = containerPath },
            CloudSandboxRpcs.PathStat,
            state.SessionId);
        var reply = await _client!.PathStatAsync(
                env, BuildHeaders(state.SessionId), cancellationToken: WithTimeout(cancellationToken))
            .ConfigureAwait(false);
        return CloudSandboxSecure.Open(
            key, reply, PathStatResponse.Parser, CloudSandboxRpcs.PathStat, state.SessionId);
    }

    private async Task<string> CreateSessionAsync(string roomId, CancellationToken cancellationToken)
    {
        EnsureClient();
        var key = RequireKey();
        var req = CloudSandboxSecure.Seal(
            key,
            new CreateSessionRequest
            {
                RoomId = roomId,
                ImageTag = string.IsNullOrWhiteSpace(_settings.CloudSandbox.ImageTag)
                    ? RoomDockerSandbox.ImageName
                    : _settings.CloudSandbox.ImageTag.Trim(),
                Region = string.IsNullOrWhiteSpace(_settings.CloudSandbox.PreferredRegion)
                    ? ""
                    : _settings.CloudSandbox.PreferredRegion.Trim(),
                NetworkPolicy = string.IsNullOrWhiteSpace(_settings.CloudSandbox.NetworkPolicy)
                    ? "HttpsEgress"
                    : _settings.CloudSandbox.NetworkPolicy.Trim()
            },
            CloudSandboxRpcs.CreateSession);

        try
        {
            var reply = await _client!.CreateSessionAsync(
                    req, BuildHeaders(), cancellationToken: WithTimeout(cancellationToken))
                .ConfigureAwait(false);
            var created = CloudSandboxSecure.Open(
                key, reply, CreateSessionResponse.Parser, CloudSandboxRpcs.CreateSession);
            if (string.IsNullOrWhiteSpace(created.SessionId))
            {
                throw new InvalidOperationException("云沙箱未返回 sessionId。");
            }

            return created.SessionId;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
        {
            throw new InvalidOperationException("云沙箱配额不足（企业网关），无法创建会话。", ex);
        }
    }

    private async Task PushSyncAsync(
        string sessionId,
        string workspaceRoot,
        string skillsRoot,
        IReadOnlyList<string>? skillNames,
        CancellationToken cancellationToken)
    {
        await PushKindAsync(sessionId, "workspace", workspaceRoot, onlyTopLevelNames: null, cancellationToken)
            .ConfigureAwait(false);
        var selectedFolders = CloudWorkspaceSync.ResolveSelectedSkillFolders(skillsRoot, skillNames);
        await PushKindAsync(sessionId, "skills", skillsRoot, selectedFolders, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PushKindAsync(
        string sessionId,
        string kind,
        string hostRoot,
        IReadOnlyList<string>? onlyTopLevelNames,
        CancellationToken cancellationToken)
    {
        if (_settings.CloudSandbox.PreferIncrementalSync)
        {
            try
            {
                if (await TryPushIncrementalAsync(sessionId, kind, hostRoot, onlyTopLevelNames, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Incremental sync unavailable for {Kind}; falling back to full tar.", kind);
            }
        }

        await using var tar = CloudWorkspaceSync.PackDirectoryTarGz(hostRoot, onlyTopLevelNames);
        await UploadSyncFullAsync(sessionId, kind, tar, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryPushIncrementalAsync(
        string sessionId,
        string kind,
        string hostRoot,
        IReadOnlyList<string>? onlyTopLevelNames,
        CancellationToken cancellationToken)
    {
        EnsureClient();
        var key = RequireKey();
        var manifest = CloudWorkspaceSync.BuildManifest(hostRoot, onlyTopLevelNames);
        var req = new SyncManifestRequest
        {
            SessionId = sessionId,
            Kind = kind
        };
        foreach (var entry in manifest)
        {
            req.Entries.Add(new FileManifestEntry
            {
                Path = entry.Path,
                Size = entry.Size,
                MtimeUnixMs = entry.MtimeUnixMs,
                Sha256 = entry.Sha256
            });
        }

        var sealedReq = CloudSandboxSecure.Seal(key, req, CloudSandboxRpcs.SyncManifest, sessionId);
        SyncManifestResponse parsed;
        try
        {
            var reply = await _client!.SyncManifestAsync(
                    sealedReq, BuildHeaders(sessionId), cancellationToken: WithTimeout(cancellationToken))
                .ConfigureAwait(false);
            parsed = CloudSandboxSecure.Open(
                key, reply, SyncManifestResponse.Parser, CloudSandboxRpcs.SyncManifest, sessionId);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unimplemented or StatusCode.NotFound)
        {
            return false;
        }

        var needUpload = new HashSet<string>(parsed.NeedUpload, StringComparer.Ordinal);
        using var call = _client!.SyncDelta(
            BuildHeaders(sessionId),
            cancellationToken: WithTimeout(cancellationToken));

        var header = new SyncDeltaRequest
        {
            Header = new SyncDeltaHeader
            {
                SessionId = sessionId,
                Kind = kind
            }
        };
        foreach (var entry in manifest)
        {
            header.Header.Entries.Add(new FileManifestEntry
            {
                Path = entry.Path,
                Size = entry.Size,
                MtimeUnixMs = entry.MtimeUnixMs,
                Sha256 = entry.Sha256
            });
        }

        await call.RequestStream.WriteAsync(
                CloudSandboxSecure.Seal(key, header, CloudSandboxRpcs.SyncDelta, sessionId),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in manifest)
        {
            if (!needUpload.Contains(entry.Path))
            {
                continue;
            }

            var full = Path.Combine(hostRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                throw new InvalidOperationException($"增量同步缺少本地文件：{entry.Path}");
            }

            var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            // Stream blob in one or more SecureEnvelope messages (path + content).
            for (var i = 0; i < Math.Max(bytes.Length, 1); i += StreamChunk)
            {
                var len = bytes.Length == 0 ? 0 : Math.Min(StreamChunk, bytes.Length - i);
                var blob = new SyncDeltaRequest
                {
                    Blob = new SyncDeltaBlob
                    {
                        Path = entry.Path,
                        Content = bytes.Length == 0
                            ? ByteString.Empty
                            : ByteString.CopyFrom(bytes, i, len)
                    }
                };
                // For multi-chunk same path, server last-write wins — send whole file in chunks by concatenating.
                // Better: send one blob per file. Keep single blob for typical sizes.
                if (i == 0 && bytes.Length <= StreamChunk)
                {
                    await call.RequestStream.WriteAsync(
                            CloudSandboxSecure.Seal(key, blob, CloudSandboxRpcs.SyncDelta, sessionId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                if (i == 0)
                {
                    // Send entire file as one blob for correctness with apply_delta.
                    blob.Blob.Content = ByteString.CopyFrom(bytes);
                    await call.RequestStream.WriteAsync(
                            CloudSandboxSecure.Seal(key, blob, CloudSandboxRpcs.SyncDelta, sessionId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
            }
        }

        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        _ = await call.ResponseAsync.ConfigureAwait(false);
        return true;
    }

    private async Task UploadSyncFullAsync(
        string sessionId,
        string kind,
        Stream tarGz,
        CancellationToken cancellationToken)
    {
        EnsureClient();
        var key = RequireKey();
        using var call = _client!.SyncFull(
            BuildHeaders(sessionId),
            cancellationToken: WithTimeout(cancellationToken));
        await call.RequestStream.WriteAsync(
                CloudSandboxSecure.Seal(
                    key,
                    new SyncFullChunk
                    {
                        Header = new SyncFullHeader { SessionId = sessionId, Kind = kind }
                    },
                    CloudSandboxRpcs.SyncFull,
                    sessionId),
                cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[StreamChunk];
        int read;
        while ((read = await tarGz.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            await call.RequestStream.WriteAsync(
                    CloudSandboxSecure.Seal(
                        key,
                        new SyncFullChunk { Data = ByteString.CopyFrom(buffer, 0, read) },
                        CloudSandboxRpcs.SyncFull,
                        sessionId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        _ = await call.ResponseAsync.ConfigureAwait(false);
    }

    private async Task DeleteSessionQuietAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(TryGetApiKey()) || string.IsNullOrWhiteSpace(_settings.CloudSandbox.SharedKey))
            {
                return;
            }

            EnsureClient();
            var key = RequireKey();
            var env = CloudSandboxSecure.Seal(
                key,
                new DeleteSessionRequest { SessionId = sessionId },
                CloudSandboxRpcs.DeleteSession,
                sessionId);
            await _client!.DeleteSessionAsync(
                    env, BuildHeaders(sessionId), cancellationToken: WithTimeout(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cloud session delete failed for {SessionId}", sessionId);
        }
    }

    private void EnsureClient()
    {
        if (_client is not null && _channel is not null && _sharedKey is not null)
        {
            return;
        }

        var baseUrl = _settings.CloudSandbox.BaseUrl.Trim().TrimEnd('/');
        _sharedKey = AesGcmFrame.ParseSharedKeyBase64(_settings.CloudSandbox.SharedKey);
        _channel = GrpcChannel.ForAddress(baseUrl, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 64 * 1024 * 1024,
            MaxSendMessageSize = 64 * 1024 * 1024,
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan
            }
        });
        _client = new global::Athlon.Agent.CloudSandbox.V1.CloudSandbox.CloudSandboxClient(_channel);
    }

    private Metadata BuildHeaders(string? sessionId = null)
    {
        var md = new Metadata
        {
            { "x-api-key", RequireApiKey() },
            { "authorization", "Bearer " + RequireApiKey() }
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            md.Add("x-session-id", sessionId);
        }

        return md;
    }

    private CancellationToken WithTimeout(CancellationToken cancellationToken)
    {
        // Call sites pass this into gRPC; deadline is also applied via linked CTS lifetime of the call.
        var timeoutSec = Math.Clamp(_settings.CloudSandbox.RequestTimeoutSeconds, 30, 600);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        // Caller does not dispose; acceptable for short-lived RPCs. Prefer deadline on CallOptions
        // when migrating further streaming UX.
        return linked.Token;
    }

    private byte[] RequireKey()
    {
        EnsureClient();
        return _sharedKey!;
    }

    private string? TryGetApiKey()
    {
        var key = _settings.CloudSandbox.ApiKey?.Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private string RequireApiKey() =>
        TryGetApiKey()
        ?? throw new InvalidOperationException("云沙箱需要配置 CloudSandbox:ApiKey。");

    private CloudSessionState RequireSession(string roomId)
    {
        if (_sessions.TryGetValue(roomId, out var state))
        {
            return state;
        }

        throw new InvalidOperationException($"云沙箱会话未就绪（room={roomId}）。请先 EnsureRoomSession。");
    }

    /// <summary>Kept for tests that still assert ASCII-safe blob naming helpers.</summary>
    internal static string EncodeBlobFieldName(string relativePath)
    {
        var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(relativePath))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "blob:" + token;
    }

    private sealed record CloudSessionState(string SessionId, string WorkspaceRoot, string SkillsRoot);
}
