using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
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
    private readonly HttpClient _http;
    private readonly ILogger<RoomCloudSandbox> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CloudSessionState> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public RoomCloudSandbox(
        AppSettings settings,
        ILogger<RoomCloudSandbox> logger,
        HttpClient? httpClient = null)
    {
        _settings = settings;
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
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
                Message = "未配置云沙箱服务（CloudSandbox:BaseUrl）。",
                QuotaHint = null
            };
        }

        var apiKey = TryGetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new RoomSandboxProbeResult
            {
                Available = false,
                ImageReady = false,
                Message = "云沙箱需要配置 CloudSandbox:ApiKey（经企业网关鉴权/计量）。",
                QuotaHint = null
            };
        }

        try
        {
            using var req = CreateRequest(HttpMethod.Get, "/v1/status");
            using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.PaymentRequired || (int)res.StatusCode == 429)
            {
                return new RoomSandboxProbeResult
                {
                    Available = false,
                    ImageReady = false,
                    Message = "云沙箱配额不足（企业网关），请稍后重试或联系管理员。",
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

        using var req = CreateRequest(HttpMethod.Get, $"/v1/sessions/{Uri.EscapeDataString(state.SessionId)}/sync/pull");
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
            $"/v1/sessions/{Uri.EscapeDataString(state.SessionId)}/exec");
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
        using var req = CreateRequest(HttpMethod.Get, url);
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
        using var req = CreateRequest(HttpMethod.Put, url);
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
                : _settings.CloudSandbox.ImageTag.Trim(),
            region = string.IsNullOrWhiteSpace(_settings.CloudSandbox.PreferredRegion)
                ? null
                : _settings.CloudSandbox.PreferredRegion.Trim(),
            networkPolicy = string.IsNullOrWhiteSpace(_settings.CloudSandbox.NetworkPolicy)
                ? "HttpsEgress"
                : _settings.CloudSandbox.NetworkPolicy.Trim()
        };

        using var req = CreateRequest(HttpMethod.Post, "/v1/sessions");
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.PaymentRequired || (int)res.StatusCode == 429)
        {
            throw new InvalidOperationException("云沙箱配额不足（企业网关），无法创建会话。");
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
        await PushKindAsync(sessionId, "workspace", workspaceRoot, onlyTopLevelNames: null, cancellationToken)
            .ConfigureAwait(false);
        await PushKindAsync(sessionId, "skills", skillsRoot, skillNames, cancellationToken)
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
        await UploadSyncPartAsync(sessionId, kind, tar, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryPushIncrementalAsync(
        string sessionId,
        string kind,
        string hostRoot,
        IReadOnlyList<string>? onlyTopLevelNames,
        CancellationToken cancellationToken)
    {
        var manifest = CloudWorkspaceSync.BuildManifest(hostRoot, onlyTopLevelNames);
        using (var req = CreateRequest(
                   HttpMethod.Post,
                   $"/v1/sessions/{Uri.EscapeDataString(sessionId)}/sync/manifest?kind={Uri.EscapeDataString(kind)}"))
        {
            req.Content = new StringContent(
                CloudWorkspaceSync.SerializeManifest(manifest),
                Encoding.UTF8,
                "application/json");
            using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return false;
            }

            await EnsureSuccessAsync(res, $"同步清单 {kind}").ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            // need / needUpload = paths this desktop client must upload (push), not download.
            var needUpload = ReadNeedUploadPaths(doc.RootElement);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(kind), "kind");
            form.Add(new StringContent(CloudWorkspaceSync.SerializeManifest(manifest)), "manifest");
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
                // ASCII-safe field name: blob:<base64url(utf8 path)> — avoids non-ASCII multipart filename breakage.
                var fieldName = EncodeBlobFieldName(entry.Path);
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, fieldName, "bin");
            }

            using var deltaReq = CreateRequest(
                HttpMethod.Post,
                $"/v1/sessions/{Uri.EscapeDataString(sessionId)}/sync/delta");
            deltaReq.Content = form;
            using var deltaRes = await SendAsync(deltaReq, cancellationToken).ConfigureAwait(false);
            if (deltaRes.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return false;
            }

            await EnsureSuccessAsync(deltaRes, $"增量同步 {kind}").ConfigureAwait(false);
            return true;
        }
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
            $"/v1/sessions/{Uri.EscapeDataString(sessionId)}/sync");
        req.Content = content;
        using var res = await SendAsync(req, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(res, $"同步 {kind}").ConfigureAwait(false);
    }

    private async Task DeleteSessionQuietAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(TryGetApiKey()))
            {
                return;
            }

            using var req = CreateRequest(
                HttpMethod.Delete,
                $"/v1/sessions/{Uri.EscapeDataString(sessionId)}");
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

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var baseUrl = _settings.CloudSandbox.BaseUrl.Trim().TrimEnd('/');
        var req = new HttpRequestMessage(method, baseUrl + relativePath);
        var apiKey = RequireApiKey();
        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    private string? TryGetApiKey()
    {
        var key = _settings.CloudSandbox.ApiKey?.Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private string RequireApiKey() =>
        TryGetApiKey()
        ?? throw new InvalidOperationException("云沙箱需要配置 CloudSandbox:ApiKey（企业网关）。");

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
            throw new InvalidOperationException($"{action}失败：API Key 无效或企业网关拒绝（配额以网关为准）。");
        }

        throw new InvalidOperationException($"{action}失败（{(int)res.StatusCode}）：{Truncate(body, 240)}");
    }

    /// <summary>Multipart field name carrying a UTF-8 path without relying on Content-Disposition filename.</summary>
    internal static string EncodeBlobFieldName(string relativePath)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(relativePath))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "blob:" + token;
    }

    /// <summary>
    /// Paths the desktop must upload so control-plane staging matches the desired manifest.
    /// Prefers <c>needUpload</c>; falls back to legacy <c>need</c>.
    /// </summary>
    private static HashSet<string> ReadNeedUploadPaths(JsonElement root)
    {
        var needUpload = new HashSet<string>(StringComparer.Ordinal);
        JsonElement needEl;
        if (root.TryGetProperty("needUpload", out var modern) && modern.ValueKind == JsonValueKind.Array)
        {
            needEl = modern;
        }
        else if (root.TryGetProperty("need", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
        {
            needEl = legacy;
        }
        else
        {
            return needUpload;
        }

        foreach (var e in needEl.EnumerateArray())
        {
            var p = e.GetString();
            if (!string.IsNullOrWhiteSpace(p))
            {
                needUpload.Add(p);
            }
        }

        return needUpload;
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
