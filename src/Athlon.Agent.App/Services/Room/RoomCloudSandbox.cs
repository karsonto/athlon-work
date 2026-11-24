using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;
using Microsoft.Extensions.Logging;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Cloud sandbox backend: Imp SSO bearer + control-plane HTTP API.</summary>
public sealed class RoomCloudSandbox : IRoomSandboxBackend, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppSettings _settings;
    private readonly IImpSsoSessionStore _ssoSessionStore;
    private readonly HttpClient _http;
    private readonly ILogger<RoomCloudSandbox> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CloudSessionState> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public RoomCloudSandbox(
        AppSettings settings,
        IImpSsoSessionStore ssoSessionStore,
        ILogger<RoomCloudSandbox> logger,
        HttpClient? httpClient = null)
    {
        _settings = settings;
        _ssoSessionStore = ssoSessionStore;
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<RoomSandboxProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var cloud = _settings.CloudSandbox;
        if (!cloud.IsConfigured)
        {
            return new RoomSandboxProbeResult
            {
                Available = false,
                ImageReady = false,
                Message = "未配置云沙箱服务（CloudSandbox:BaseUrl）。",
                QuotaHint = null
            };
        }

        var token = TryGetSsoToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new RoomSandboxProbeResult
            {
                Available = false,
                ImageReady = false,
                Message = "云沙箱需要先登录 Imp SSO。",
                QuotaHint = null
            };
        }

        try
        {
            using var req = CreateRequest(HttpMethod.Get, "/v1/status", token);
            using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.PaymentRequired || (int)res.StatusCode == 429)
            {
                return new RoomSandboxProbeResult
                {
                    Available = false,
                    ImageReady = false,
                    Message = "云沙箱配额不足，请稍后重试或联系管理员。",
                    QuotaHint = Truncate(body, 200)
                };
            }

            if (!res.IsSuccessStatusCode)
            {
                return new RoomSandboxProbeResult
                {
                    Available = false,
                    ImageReady = false,
                    Message = $"云沙箱不可用（{(int)res.StatusCode}）：{Truncate(body, 160)}",
                    QuotaHint = null
                };
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            var available = !root.TryGetProperty("available", out var av) || av.ValueKind != JsonValueKind.False;
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            var quota = root.TryGetProperty("quotaHint", out var qh) ? qh.GetString() : null;
            string[]? hints = null;
            if (root.TryGetProperty("runtimeHints", out var rh) && rh.ValueKind == JsonValueKind.Array)
            {
                hints = rh.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
            }

            if (!available)
            {
                return new RoomSandboxProbeResult
                {
                    Available = false,
                    ImageReady = false,
                    Message = string.IsNullOrWhiteSpace(message) ? "云沙箱当前不可用。" : message!,
                    QuotaHint = quota
                };
            }

            return new RoomSandboxProbeResult
            {
                Available = true,
                ImageReady = true,
                Message = string.IsNullOrWhiteSpace(message)
                    ? "云沙箱可用；工作区将同步至 Athlon 云端。"
                    : message!,
                QuotaHint = quota,
                RuntimeHints = hints is { Length: > 0 }
                    ? hints
                    : ["python3.12", "java8", "node20", "chromium"]
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

        using var req = CreateRequest(HttpMethod.Get, $"/v1/sessions/{Uri.EscapeDataString(state.SessionId)}/sync/pull", RequireToken());
        using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }

        await EnsureSuccessAsync(res, "拉取云沙箱工作区").ConfigureAwait(false);
        await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        CloudWorkspaceSync.UnpackTarGzToDirectory(stream, ws);
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
        var payload = new
        {
            command,
            cwd = string.IsNullOrWhiteSpace(workingDirectory)
                ? RoomSandboxPathMap.ContainerWorkspaceRoot
                : workingDirectory,
            timeoutSec = Math.Clamp(timeoutSeconds, 1, 600)
        };

        using var req = CreateRequest(
            HttpMethod.Post,
            $"/v1/sessions/{Uri.EscapeDataString(state.SessionId)}/exec",
            RequireToken());
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(res, "执行命令", body).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;
        return new RoomSandboxExecResult
        {
            ExitCode = root.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : -1,
            Stdout = root.TryGetProperty("stdout", out var so) ? so.GetString() ?? "" : "",
            Stderr = root.TryGetProperty("stderr", out var se) ? se.GetString() ?? "" : ""
        };
    }

    public async Task<byte[]?> ReadFileAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var state = RequireSession(roomId);
        var url =
            $"/v1/sessions/{Uri.EscapeDataString(state.SessionId)}/fs?path={Uri.EscapeDataString(containerPath)}";
        using var req = CreateRequest(HttpMethod.Get, url, RequireToken());
        using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(res, "读取文件").ConfigureAwait(false);
        return await res.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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

        var url =
            $"/v1/sessions/{Uri.EscapeDataString(state.SessionId)}/fs?path={Uri.EscapeDataString(containerPath)}";
        using var req = CreateRequest(HttpMethod.Put, url, RequireToken());
        req.Content = new ByteArrayContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(res, "写入文件").ConfigureAwait(false);
    }

    public async Task EnsureDirectoryAsync(
        string roomId,
        string containerDirectory,
        CancellationToken cancellationToken = default)
    {
        var dir = string.IsNullOrWhiteSpace(containerDirectory)
            ? RoomSandboxPathMap.ContainerWorkspaceRoot
            : containerDirectory.TrimEnd('/');
        var result = await ExecAsync(
                roomId,
                $"mkdir -p -- {ShellQuote(dir)}",
                "/",
                30,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"mkdir failed: {result.Stderr}");
        }
    }

    public async Task<bool> PathExistsAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecAsync(
                roomId,
                $"test -e {ShellQuote(containerPath)}",
                "/",
                15,
                cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> IsDirectoryAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecAsync(
                roomId,
                $"test -d {ShellQuote(containerPath)}",
                "/",
                15,
                cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<IReadOnlyList<string>> ListDirectoryAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecAsync(
                roomId,
                $"ls -1A -- {ShellQuote(containerPath)}",
                "/",
                30,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return Array.Empty<string>();
        }

        return result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
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
        _http.Dispose();
    }

    private async Task<string> CreateSessionAsync(string roomId, CancellationToken cancellationToken)
    {
        var payload = new
        {
            roomId,
            imageTag = string.IsNullOrWhiteSpace(_settings.CloudSandbox.ImageTag)
                ? RoomDockerSandbox.ImageName
                : _settings.CloudSandbox.ImageTag.Trim()
        };

        using var req = CreateRequest(HttpMethod.Post, "/v1/sessions", RequireToken());
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.PaymentRequired || (int)res.StatusCode == 429)
        {
            throw new InvalidOperationException("云沙箱配额不足，无法创建会话。");
        }

        await EnsureSuccessAsync(res, "创建云沙箱会话", body).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var sessionId = doc.RootElement.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("云沙箱未返回 sessionId。");
        }

        return sessionId!;
    }

    private async Task PushSyncAsync(
        string sessionId,
        string workspaceRoot,
        string skillsRoot,
        IReadOnlyList<string>? skillNames,
        CancellationToken cancellationToken)
    {
        await using var workspaceTar = CloudWorkspaceSync.PackDirectoryTarGz(workspaceRoot);
        await UploadSyncPartAsync(sessionId, "workspace", workspaceTar, cancellationToken).ConfigureAwait(false);

        await using var skillsTar = CloudWorkspaceSync.PackDirectoryTarGz(skillsRoot, skillNames);
        await UploadSyncPartAsync(sessionId, "skills", skillsTar, cancellationToken).ConfigureAwait(false);
    }

    private async Task UploadSyncPartAsync(
        string sessionId,
        string kind,
        Stream tarGz,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(tarGz);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        content.Add(streamContent, "archive", $"{kind}.tar.gz");
        content.Add(new StringContent(kind), "kind");

        using var req = CreateRequest(
            HttpMethod.Post,
            $"/v1/sessions/{Uri.EscapeDataString(sessionId)}/sync",
            RequireToken());
        req.Content = content;
        using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(res, $"同步 {kind}").ConfigureAwait(false);
    }

    private async Task DeleteSessionQuietAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var token = TryGetSsoToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            using var req = CreateRequest(
                HttpMethod.Delete,
                $"/v1/sessions/{Uri.EscapeDataString(sessionId)}",
                token);
            using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.NotFound)
            {
                var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Cloud session delete {SessionId} => {Status}: {Body}", sessionId, res.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cloud session delete failed for {SessionId}", sessionId);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath, string bearerToken)
    {
        var baseUrl = _settings.CloudSandbox.BaseUrl.Trim().TrimEnd('/');
        var req = new HttpRequestMessage(method, baseUrl + relativePath);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timeoutSec = Math.Clamp(_settings.CloudSandbox.RequestTimeoutSeconds, 30, 600);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
    }

    private string? TryGetSsoToken()
    {
        var session = _ssoSessionStore.GetCachedSession();
        if (session is null || string.IsNullOrWhiteSpace(session.SsoToken))
        {
            return null;
        }

        if (_ssoSessionStore.IsExpired(session))
        {
            return null;
        }

        return session.SsoToken.Trim();
    }

    private string RequireToken() =>
        TryGetSsoToken()
        ?? throw new InvalidOperationException("云沙箱需要有效的 Imp SSO 登录会话。");

    private CloudSessionState RequireSession(string roomId)
    {
        if (_sessions.TryGetValue(roomId, out var state))
        {
            return state;
        }

        throw new InvalidOperationException($"云沙箱会话未就绪（room={roomId}）。请先 EnsureRoomSession。");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage res,
        string action,
        string? body = null)
    {
        if (res.IsSuccessStatusCode)
        {
            return;
        }

        body ??= await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.PaymentRequired || (int)res.StatusCode == 429)
        {
            throw new InvalidOperationException($"{action}失败：云沙箱配额不足。");
        }

        if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"{action}失败：SSO 鉴权无效，请重新登录。");
        }

        throw new InvalidOperationException($"{action}失败（{(int)res.StatusCode}）：{Truncate(body, 240)}");
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private sealed record CloudSessionState(string SessionId, string WorkspaceRoot, string SkillsRoot);
}
