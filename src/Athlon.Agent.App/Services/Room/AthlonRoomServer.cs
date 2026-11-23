using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Athlon.Agent.Core;
using Athlon.Agent.Skills;
using Microsoft.Extensions.Logging;

namespace Athlon.Agent.App.Services.Room;

/// <summary>
/// LAN-bound HTTP server for Athlon chat rooms (static SPA + REST + SSE).
/// </summary>
public sealed class AthlonRoomServer : IAsyncDisposable
{
    public const int DefaultPort = 6890;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ico"] = "image/x-icon",
        [".map"] = "application/json"
    };

    private readonly RoomStore _store;
    private readonly AppSettings _settings;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly RoomAgentProfileStore _profileStore;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly ILogger<AthlonRoomServer> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _sseConnections = new();
    private readonly ConcurrentDictionary<string, byte> _activeRoomIds = new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private volatile bool _listening;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _port = DefaultPort;
    private string? _lanIp;
    private bool _lanReachable;
    private string? _bindWarning;
    private Action<string>? _onRoomOpened;
    private Action<string>? _onRoomClosed;

    public AthlonRoomServer(
        RoomStore store,
        AppSettings settings,
        IActiveWorkspaceContext workspaceContext,
        RoomAgentProfileStore profileStore,
        IAgentSkillCatalog skillCatalog,
        ILogger<AthlonRoomServer> logger)
    {
        _store = store;
        _settings = settings;
        _workspaceContext = workspaceContext;
        _profileStore = profileStore;
        _skillCatalog = skillCatalog;
        _logger = logger;
    }

    public RoomInfo? SetRoomTitle(string roomId, string title) =>
        _store.UpdateTitle(roomId, title);

    public bool IsRunning => _listening && _listener is not null;

    public int Port => _port;

    public string? LanIp => _lanIp;

    /// <summary>
    /// True when bound to all interfaces (0.0.0.0) so LAN/peers can reach the invite host.
    /// False after fallback to 127.0.0.1 only.
    /// </summary>
    public bool IsLanReachable => _lanReachable;

    /// <summary>Human-readable bind warning for UI when <see cref="IsLanReachable"/> is false.</summary>
    public string? BindWarning => _bindWarning;

    /// <summary>Most recently created/focused room (compat).</summary>
    public string? ActiveRoomId =>
        _activeRoomIds.Keys.LastOrDefault();

    public IReadOnlyList<string> ActiveRoomIds => _activeRoomIds.Keys.ToArray();

    public string? ActiveInviteUrl
    {
        get
        {
            var id = ActiveRoomId;
            return id is null ? null : GetInviteUrl(id);
        }
    }

    public RoomInfo? ActiveRoom
    {
        get
        {
            var id = ActiveRoomId;
            return id is null ? null : _store.GetRoom(id);
        }
    }

    public void SetRoomLifecycleHooks(Action<string>? onOpened, Action<string>? onClosed)
    {
        _onRoomOpened = onOpened;
        _onRoomClosed = onClosed;
    }

    /// <summary>
    /// Shareable invite for LAN/peers. Null when bound loopback-only (link would point at an unbound address).
    /// </summary>
    public string? GetInviteUrl(string roomId)
    {
        if (!_lanReachable || _lanIp is null || !_activeRoomIds.ContainsKey(roomId))
        {
            return null;
        }

        var room = _store.GetRoom(roomId);
        return room is null ? null : BuildInviteUrl(_lanIp, _port, room.Id, room.Token);
    }

    /// <summary>Always uses 127.0.0.1 — for host "open locally" even when LAN bind failed.</summary>
    public string? GetLocalInviteUrl(string roomId)
    {
        if (!IsRunning || !_activeRoomIds.ContainsKey(roomId))
        {
            return null;
        }

        var room = _store.GetRoom(roomId);
        return room is null ? null : BuildInviteUrl("127.0.0.1", _port, room.Id, room.Token);
    }

    public string BindHint =>
        $"Ensure nothing else is using TCP port {_port}, then reopen the chat room.";

    public IReadOnlyList<RoomSummary> ListActiveRooms()
    {
        if (!IsRunning)
        {
            return [];
        }

        var list = new List<RoomSummary>();
        foreach (var id in _activeRoomIds.Keys)
        {
            var room = _store.GetRoom(id);
            if (room is null)
            {
                continue;
            }

            list.Add(new RoomSummary
            {
                Id = room.Id,
                Title = room.Title,
                InviteUrl = GetInviteUrl(room.Id) ?? string.Empty,
                CreatedAt = room.CreatedAt,
                WorkspaceRoot = room.WorkspaceRoot,
                WorkspaceId = room.WorkspaceId,
                WorkspaceName = ResolveWorkspaceName(room.WorkspaceId, room.WorkspaceRoot)
            });
        }

        return list.OrderByDescending(r => r.CreatedAt).ToArray();
    }

    public async Task<RoomCreateResult> StartOrCreateRoomAsync(
        string? title = null,
        string? workspaceRoot = null,
        string? workspaceId = null,
        int? port = null,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _port = port is > 0 and < 65535 ? port.Value : DefaultPort;
            var (root, id) = ResolveWorkspaceInputs(workspaceRoot, workspaceId);
            var room = _store.CreateRoom(title, root, id);
            _activeRoomIds[room.Id] = 0;

            if (!IsRunning)
            {
                await StartListenerAsync(cancellationToken).ConfigureAwait(false);
            }

            var invite = GetInviteUrl(room.Id) ?? GetLocalInviteUrl(room.Id) ?? string.Empty;
            try
            {
                _onRoomOpened?.Invoke(room.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Room opened hook failed.");
            }

            return new RoomCreateResult
            {
                Room = room,
                InviteUrl = invite,
                LanIp = _lanIp ?? "127.0.0.1",
                Port = _port
            };
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<bool> CloseRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_activeRoomIds.TryRemove(roomId, out _))
            {
                return false;
            }

            try
            {
                _store.InvalidateToken(roomId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to invalidate room token.");
            }

            try
            {
                _onRoomClosed?.Invoke(roomId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Room closed hook failed.");
            }

            if (_activeRoomIds.IsEmpty)
            {
                await StopListenerCoreAsync().ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public RoomInfo? SetRoomWorkspace(string roomId, string? workspaceRoot, string? workspaceId = null)
    {
        var (root, id) = ResolveWorkspaceInputs(workspaceRoot, workspaceId, allowEmpty: false);
        return _store.UpdateWorkspace(roomId, root, id);
    }

    /// <returns>null if ok; otherwise an error code such as workspace_not_found.</returns>
    public string? ValidateWorkspaceRoot(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(workspaceRoot.Trim());
            return Directory.Exists(full) ? null : "workspace_not_found";
        }
        catch
        {
            return "workspace_not_found";
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var id in _activeRoomIds.Keys.ToArray())
            {
                _activeRoomIds.TryRemove(id, out _);
                try
                {
                    _store.InvalidateToken(id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to invalidate room token.");
                }

                try
                {
                    _onRoomClosed?.Invoke(id);
                }
                catch
                {
                    // ignore
                }
            }

            await StopListenerCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StartListenerAsync(CancellationToken cancellationToken)
    {
        var contentRoot = AthlonRoomAssets.AssetsDirectory;
        if (!Directory.Exists(contentRoot))
        {
            Directory.CreateDirectory(contentRoot);
        }

        // TcpListener on 0.0.0.0 — no Windows Http.sys URL ACL required.
        Exception? lastError = null;
        foreach (var (address, lanReachable) in BuildBindCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(address, _port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                _listener = listener;
                _listening = true;
                _lanReachable = lanReachable;
                _lanIp = lanReachable
                    ? (ResolveLanIpv4() ?? "127.0.0.1")
                    : "127.0.0.1";
                _bindWarning = lanReachable ? null : BuildLoopbackOnlyWarning();

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => AcceptLoopAsync(listener, contentRoot, token), token);

                if (!_lanReachable)
                {
                    _logger.LogWarning(
                        "AthlonRoom TcpListener bound loopback-only on 127.0.0.1:{Port}. {Warning}",
                        _port,
                        _bindWarning);
                }
                else
                {
                    _logger.LogInformation(
                        "AthlonRoom TcpListener on 0.0.0.0:{Port} (invite host {LanIp})",
                        _port,
                        _lanIp);
                }

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _listening = false;
                _logger.LogWarning(ex, "AthlonRoom TcpListener bind failed on {Address}:{Port}", address, _port);
                try
                {
                    listener?.Stop();
                }
                catch
                {
                    // ignore
                }
            }
        }

        throw new InvalidOperationException(
            $"Unable to bind AthlonRoom TcpListener on port {_port}. {BindHint}",
            lastError);
    }

    private static IEnumerable<(IPAddress Address, bool LanReachable)> BuildBindCandidates()
    {
        yield return (IPAddress.Any, true);
        yield return (IPAddress.Loopback, false);
    }

    private string BuildLoopbackOnlyWarning() =>
        $"Room server is listening on 127.0.0.1:{_port} only — LAN/peer invites will not work. {BindHint}";

    private async Task StopListenerCoreAsync()
    {
        _lanReachable = false;
        _bindWarning = null;
        _lanIp = null;
        _listening = false;
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // ignore
            }

            cts.Dispose();
        }

        var listener = _listener;
        _listener = null;
        if (listener is not null)
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // ignore
            }
        }

        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            try
            {
                await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, string contentRoot, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listening)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested || !_listening)
                {
                    break;
                }

                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, contentRoot, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, string contentRoot, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var context = await RoomTcpHttp.AcceptRequestAsync(client, cancellationToken).ConfigureAwait(false);
                if (context is null)
                {
                    return;
                }

                await HandleRequestAsync(context, contentRoot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AthlonRoom client handler failed.");
            }
        }
    }

    private async Task HandleRequestAsync(RoomHttpContext context, string contentRoot)
    {
        try
        {
            AddCors(context.Response);
            if (string.Equals(context.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }

            var path = context.Request.Path ?? "/";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleApiAsync(context).ConfigureAwait(false);
                return;
            }

            // SPA routes: /r/{roomId}
            await ServeStaticAsync(context, contentRoot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Room request failed.");
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch
            {
                // best effort
            }
        }
    }

    private async Task HandleApiAsync(RoomHttpContext context)
    {
        var method = context.Request.Method ?? "GET";
        var path = context.Request.Path ?? "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // GET /api/workspaces — localhost only
        if (segments.Length == 2
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("workspaces", StringComparison.OrdinalIgnoreCase)
            && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLocalRequest(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Forbidden, new { error = "local_only" }).ConfigureAwait(false);
                return;
            }

            var workspaces = _settings.Workspaces
                .Where(w => !string.IsNullOrWhiteSpace(w.RootPath))
                .Select(w => new
                {
                    id = w.Id,
                    name = string.IsNullOrWhiteSpace(w.Name) ? w.RootPath : w.Name,
                    rootPath = w.RootPath,
                    kind = w.Kind
                })
                .ToArray();
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { workspaces }).ConfigureAwait(false);
            return;
        }

        // GET/POST /api/rooms — local manage (list / create)
        if (segments.Length == 2
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("rooms", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLocalRequest(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Forbidden, new { error = "local_only" }).ConfigureAwait(false);
                return;
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var rooms = ListActiveRooms();
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
                {
                    lanIp = _lanIp,
                    port = _port,
                    lanReachable = _lanReachable,
                    bindWarning = _bindWarning,
                    bindHint = BindHint,
                    rooms
                }).ConfigureAwait(false);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                string? title = null;
                string? workspaceRoot = null;
                string? workspaceId = null;
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("title", out var t))
                        {
                            title = t.GetString();
                        }

                        if (doc.RootElement.TryGetProperty("workspaceRoot", out var wr))
                        {
                            workspaceRoot = wr.GetString();
                        }

                        if (doc.RootElement.TryGetProperty("workspaceId", out var wid))
                        {
                            workspaceId = wid.GetString();
                        }
                    }
                }
                catch
                {
                    // ignore body parse
                }

                var result = await StartOrCreateRoomAsync(title, workspaceRoot, workspaceId).ConfigureAwait(false);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
                {
                    id = result.Room.Id,
                    title = result.Room.Title,
                    inviteUrl = result.InviteUrl,
                    lanIp = result.LanIp,
                    port = result.Port,
                    createdAt = result.Room.CreatedAt,
                    token = result.Room.Token,
                    workspaceRoot = result.Room.WorkspaceRoot,
                    workspaceId = result.Room.WorkspaceId,
                    workspaceName = ResolveWorkspaceName(result.Room.WorkspaceId, result.Room.WorkspaceRoot)
                }).ConfigureAwait(false);
                return;
            }
        }

        // /api/rooms/{id}/...
        if (segments.Length < 3
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("rooms", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "not_found" }).ConfigureAwait(false);
            return;
        }

        var roomId = segments[2];
        if (!RoomStore.IsSafeId(roomId) || _store.GetRoom(roomId) is null)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "room_not_found" }).ConfigureAwait(false);
            return;
        }

        // POST /api/rooms/{id}/close — localhost only
        if (segments.Length == 4
            && segments[3].Equals("close", StringComparison.OrdinalIgnoreCase)
            && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLocalRequest(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Forbidden, new { error = "local_only" }).ConfigureAwait(false);
                return;
            }

            var closed = await CloseRoomAsync(roomId).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { closed }).ConfigureAwait(false);
            return;
        }

        // POST /api/rooms/{id}/workspace — localhost only
        if (segments.Length == 4
            && segments[3].Equals("workspace", StringComparison.OrdinalIgnoreCase)
            && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLocalRequest(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Forbidden, new { error = "local_only" }).ConfigureAwait(false);
                return;
            }

            string? workspaceRoot = null;
            string? workspaceId = null;
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("workspaceRoot", out var wr))
                    {
                        workspaceRoot = wr.GetString();
                    }

                    if (doc.RootElement.TryGetProperty("workspaceId", out var wid))
                    {
                        workspaceId = wid.GetString();
                    }
                }
            }
            catch
            {
                // ignore
            }

            // Prefer explicit custom root; when only id is set, ResolveWorkspaceInputs fills root.
            var resolvedRoot = workspaceRoot;
            if (string.IsNullOrWhiteSpace(resolvedRoot) && !string.IsNullOrWhiteSpace(workspaceId))
            {
                var match = _settings.Workspaces.FirstOrDefault(w =>
                    string.Equals(w.Id, workspaceId.Trim(), StringComparison.OrdinalIgnoreCase));
                resolvedRoot = match?.RootPath;
            }

            var workspaceError = ValidateWorkspaceRoot(resolvedRoot);
            if (workspaceError is not null)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = workspaceError })
                    .ConfigureAwait(false);
                return;
            }

            var updated = SetRoomWorkspace(roomId, workspaceRoot, workspaceId);
            if (updated is null)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "room_not_found" }).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
            {
                id = updated.Id,
                workspaceRoot = updated.WorkspaceRoot,
                workspaceId = updated.WorkspaceId,
                workspaceName = ResolveWorkspaceName(updated.WorkspaceId, updated.WorkspaceRoot)
            }).ConfigureAwait(false);
            return;
        }

        // GET/PUT /api/rooms/{id}/agent-profile — localhost only
        if (segments.Length == 4
            && segments[3].Equals("agent-profile", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLocalRequest(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Forbidden, new { error = "local_only" }).ConfigureAwait(false);
                return;
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteRoomAgentProfileAsync(context.Response, roomId).ConfigureAwait(false);
                return;
            }

            if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                || method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSaveRoomAgentProfileAsync(context, roomId).ConfigureAwait(false);
                return;
            }
        }

        // POST /api/rooms/{id}/title — localhost only
        if (segments.Length == 4
            && segments[3].Equals("title", StringComparison.OrdinalIgnoreCase)
            && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLocalRequest(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Forbidden, new { error = "local_only" }).ConfigureAwait(false);
                return;
            }

            string? title = null;
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("title", out var t))
                    {
                        title = t.GetString();
                    }
                }
            }
            catch
            {
                // ignore
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "title_required" }).ConfigureAwait(false);
                return;
            }

            var renamed = SetRoomTitle(roomId, title);
            if (renamed is null)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "room_not_found" }).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
            {
                id = renamed.Id,
                title = renamed.Title
            }).ConfigureAwait(false);
            return;
        }

        var token = ExtractToken(context.Request);
        if (!_store.ValidateToken(roomId, token))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.Unauthorized, new { error = "unauthorized" }).ConfigureAwait(false);
            return;
        }

        // GET /api/rooms/{id}
        if (segments.Length == 3 && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var room = _store.GetRoom(roomId)!;
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
            {
                id = room.Id,
                title = room.Title,
                workspaceRoot = IsLocalRequest(context.Request) ? room.WorkspaceRoot : null,
                workspaceId = IsLocalRequest(context.Request) ? room.WorkspaceId : null,
                workspaceName = IsLocalRequest(context.Request)
                    ? ResolveWorkspaceName(room.WorkspaceId, room.WorkspaceRoot)
                    : null,
                createdAt = room.CreatedAt
            }).ConfigureAwait(false);
            return;
        }

        if (segments.Length >= 4)
        {
            var action = segments[3];

            // GET /api/rooms/{id}/messages?after=
            if (action.Equals("messages", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 4
                && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var after = context.Request.QueryString["after"];
                var messages = _store.GetMessages(roomId, after);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { messages }).ConfigureAwait(false);
                return;
            }

            // POST /api/rooms/{id}/messages
            if (action.Equals("messages", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 4
                && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePostMessageAsync(context, roomId).ConfigureAwait(false);
                return;
            }

            // POST /api/rooms/{id}/attachments
            if (action.Equals("attachments", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 4
                && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUploadAsync(context, roomId).ConfigureAwait(false);
                return;
            }

            // GET /api/rooms/{id}/attachments/{aid}
            if (action.Equals("attachments", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 5
                && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDownloadAsync(context, roomId, segments[4]).ConfigureAwait(false);
                return;
            }

            // GET /api/rooms/{id}/events (SSE)
            if (action.Equals("events", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 4
                && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSseAsync(context, roomId).ConfigureAwait(false);
                return;
            }
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "not_found" }).ConfigureAwait(false);
    }

    private async Task HandlePostMessageAsync(RoomHttpContext context, string roomId)
    {
        PostMessageRequest? req;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            req = JsonSerializer.Deserialize<PostMessageRequest>(body, JsonOptions);
        }
        catch
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "invalid_json" }).ConfigureAwait(false);
            return;
        }

        if (req is null || string.IsNullOrWhiteSpace(req.DisplayName))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "display_name_required" }).ConfigureAwait(false);
            return;
        }

        var attachments = new List<RoomAttachmentInfo>();
        if (req.AttachmentIds is { Count: > 0 })
        {
            foreach (var aid in req.AttachmentIds)
            {
                var meta = _store.GetAttachmentMeta(roomId, aid);
                if (meta is null)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "attachment_not_found", id = aid })
                        .ConfigureAwait(false);
                    return;
                }

                attachments.Add(meta);
            }
        }

        var hasText = !string.IsNullOrWhiteSpace(req.Text);
        if (!hasText && attachments.Count == 0)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "empty_message" }).ConfigureAwait(false);
            return;
        }

        var kind = attachments.Count == 0
            ? "text"
            : attachments.All(a => a.IsImage) && !hasText
                ? "image"
                : attachments.Count > 0 && !hasText
                    ? "file"
                    : "text";

        var message = new RoomMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SenderId = string.IsNullOrWhiteSpace(req.SenderId) ? Guid.NewGuid().ToString("N")[..8] : req.SenderId.Trim(),
            DisplayName = req.DisplayName.Trim(),
            Kind = kind,
            Text = hasText ? req.Text!.Trim() : null,
            Attachments = attachments,
            CreatedAt = DateTimeOffset.UtcNow
        };

        message = _store.AppendMessage(roomId, message);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, message).ConfigureAwait(false);
    }

    private async Task HandleUploadAsync(RoomHttpContext context, string roomId)
    {
        try
        {
            var contentType = context.Request.ContentType ?? "";
            string fileName;
            string mime;
            Stream contentStream;

            if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = await ParseMultipartAsync(context.Request).ConfigureAwait(false);
                if (parsed is null)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "invalid_multipart" }).ConfigureAwait(false);
                    return;
                }

                fileName = parsed.Value.FileName;
                mime = parsed.Value.MimeType;
                contentStream = parsed.Value.Content;
            }
            else
            {
                fileName = context.Request.QueryString["fileName"]
                    ?? context.Request.Headers["X-File-Name"]
                    ?? "upload.bin";
                mime = context.Request.QueryString["mime"]
                    ?? context.Request.ContentType
                    ?? "application/octet-stream";
                contentStream = context.Request.InputStream;
            }

            await using (contentStream)
            {
                if (context.Request.ContentLength64 > RoomStore.MaxAttachmentBytes)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.RequestEntityTooLarge, new { error = "too_large" })
                        .ConfigureAwait(false);
                    return;
                }

                var info = _store.SaveAttachment(roomId, fileName, mime, contentStream, context.Request.ContentLength64);
                var workspaceRoot = ResolveRoomWorkspaceRoot(roomId);
                if (!string.IsNullOrWhiteSpace(workspaceRoot))
                {
                    info = _store.MirrorAttachmentToWorkspace(roomId, info.Id, workspaceRoot) ?? info;
                }

                var previewUrl = $"/api/rooms/{roomId}/attachments/{info.Id}?t={Uri.EscapeDataString(ExtractToken(context.Request) ?? "")}";
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
                {
                    id = info.Id,
                    fileName = info.FileName,
                    mimeType = info.MimeType,
                    size = info.Size,
                    isImage = info.IsImage,
                    workspacePath = info.WorkspacePath,
                    previewUrl
                }).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.RequestEntityTooLarge, new { error = "too_large" }).ConfigureAwait(false);
        }
    }

    private async Task HandleDownloadAsync(RoomHttpContext context, string roomId, string attachmentId)
    {
        if (!RoomStore.IsSafeId(attachmentId))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "invalid_id" }).ConfigureAwait(false);
            return;
        }

        var meta = _store.GetAttachmentMeta(roomId, attachmentId);
        var path = _store.GetAttachmentFilePath(roomId, attachmentId);
        if (meta is null || path is null)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "not_found" }).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = meta.MimeType;
        var disposition = meta.IsImage ? "inline" : "attachment";
        context.Response.Headers["Content-Disposition"] =
            $"{disposition}; filename=\"{meta.FileName.Replace("\"", "", StringComparison.Ordinal)}\"";

        await using var stream = File.OpenRead(path);
        context.Response.ContentLength64 = stream.Length;
        context.Response.EnsureHeaders();
        await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        context.Response.Close();
    }

    private async Task HandleSseAsync(RoomHttpContext context, string roomId)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        _sseConnections[connectionId] = 0;

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";
        context.Response.Streaming = true;
        context.Response.KeepAlive = true;

        context.Response.EnsureHeaders();
        await WriteSseAsync(context.Response, "ready", new { ok = true }).ConfigureAwait(false);

        using var subscription = _store.SubscribeEvents(roomId, storeEvent =>
        {
            try
            {
                var eventName = storeEvent.Kind == RoomStoreEventKind.MessageUpdated
                    ? "message_updated"
                    : "message";
                var json = JsonSerializer.Serialize(storeEvent.Message, JsonOptions);
                var payload = $"event: {eventName}\ndata: {json}\n\n";
                var bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.WriteBodySync(bytes);
            }
            catch
            {
                // connection likely closed
            }
        });

        try
        {
            // Keep alive until client disconnects or server stops
            var buffer = new byte[1];
            while (_listening)
            {
                await Task.Delay(15000).ConfigureAwait(false);
                try
                {
                    var ping = Encoding.UTF8.GetBytes(": ping\n\n");
                    context.Response.WriteBodySync(ping);
                }
                catch
                {
                    break;
                }

                // Detect closed connection via reading (non-blocking style unavailable); ping failure exits.
                _ = buffer;
            }
        }
        catch
        {
            // client gone
        }
        finally
        {
            _sseConnections.TryRemove(connectionId, out _);
            try
            {
                context.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task ServeStaticAsync(RoomHttpContext context, string contentRoot)
    {
        var relative = ResolveStaticRelativePath(context.Request.Url, context.Request.Path);
        if (relative is null)
        {
            await WriteTextAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
            return;
        }

        var filePath = Path.Combine(contentRoot, relative);
        if (Directory.Exists(filePath))
        {
            filePath = Path.Combine(filePath, AthlonRoomAssets.EntryFileName);
        }

        // SPA fallback for /r/{roomId} only — never for real static files (.js/.css/...).
        if (!File.Exists(filePath))
        {
            var abs = context.Request.Path ?? "";
            var ext = Path.GetExtension(abs);
            if (string.IsNullOrEmpty(ext)
                && (abs.StartsWith("/r/", StringComparison.OrdinalIgnoreCase)
                    || abs.Equals("/r", StringComparison.OrdinalIgnoreCase)
                    || abs == "/"))
            {
                filePath = Path.Combine(contentRoot, AthlonRoomAssets.EntryFileName);
            }
        }

        if (!File.Exists(filePath) || !IsPathWithinRoot(contentRoot, filePath))
        {
            await WriteTextAsync(context.Response, HttpStatusCode.NotFound, "Not Found").ConfigureAwait(false);
            return;
        }

        var mime = ResolveMimeType(filePath);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = mime;
        await using var stream = File.OpenRead(filePath);
        context.Response.ContentLength64 = stream.Length;
        context.Response.EnsureHeaders();
        await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task<(string FileName, string MimeType, Stream Content)?> ParseMultipartAsync(RoomHttpRequest request)
    {
        var contentType = request.ContentType ?? "";
        var match = Regex.Match(contentType, @"boundary=(?:"")?([^"";]+)(?:"")?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var boundary = match.Groups[1].Value;
        using var ms = new MemoryStream();
        await request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
        var bytes = ms.ToArray();
        if (bytes.Length > RoomStore.MaxAttachmentBytes + 4096)
        {
            throw new InvalidOperationException($"Attachment exceeds {RoomStore.MaxAttachmentBytes} bytes.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var delimiter = "--" + boundary;
        var parts = text.Split(new[] { delimiter }, StringSplitOptions.None);
        foreach (var part in parts)
        {
            if (part.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            var trimmed = part.TrimStart('\r', '\n');
            var headerEnd = trimmed.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                continue;
            }

            var headers = trimmed[..headerEnd];
            if (!headers.Contains("filename=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nameMatch = Regex.Match(headers, @"filename=""([^""]*)""", RegexOptions.IgnoreCase);
            var fileName = nameMatch.Success ? nameMatch.Groups[1].Value : "upload.bin";
            var mimeMatch = Regex.Match(headers, @"Content-Type:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            var mime = mimeMatch.Success ? mimeMatch.Groups[1].Value.Trim() : "application/octet-stream";

            // Re-extract binary from original bytes to avoid UTF-8 corruption for images
            var headerBytes = Encoding.UTF8.GetBytes(trimmed[..(headerEnd + 4)]);
            var partStartInBody = IndexOf(bytes, Encoding.UTF8.GetBytes(trimmed[..Math.Min(64, trimmed.Length)]));
            // Fallback: locate filename section then \r\n\r\n
            var fileHeaderMarker = Encoding.UTF8.GetBytes("filename=");
            var idx = IndexOf(bytes, fileHeaderMarker);
            if (idx < 0)
            {
                var contentStart = headerEnd + 4;
                var content = trimmed[contentStart..];
                if (content.EndsWith("\r\n", StringComparison.Ordinal))
                {
                    content = content[..^2];
                }

                return (fileName, mime, new MemoryStream(Encoding.UTF8.GetBytes(content)));
            }

            var headerSep = Encoding.UTF8.GetBytes("\r\n\r\n");
            var contentBegin = IndexOf(bytes, headerSep, idx);
            if (contentBegin < 0)
            {
                continue;
            }

            contentBegin += headerSep.Length;
            var nextBoundary = Encoding.UTF8.GetBytes("\r\n--" + boundary);
            var contentEnd = IndexOf(bytes, nextBoundary, contentBegin);
            if (contentEnd < 0)
            {
                contentEnd = bytes.Length;
            }

            var length = contentEnd - contentBegin;
            if (length < 0)
            {
                continue;
            }

            var buffer = new byte[length];
            Buffer.BlockCopy(bytes, contentBegin, buffer, 0, length);
            _ = headerBytes;
            _ = partStartInBody;
            return (fileName, mime, new MemoryStream(buffer));
        }

        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? ExtractToken(RoomHttpRequest request)
    {
        var auth = request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }

        return request.QueryString["t"];
    }

    private static bool IsLocalRequest(RoomHttpRequest request)
    {
        var addr = request.RemoteEndPoint?.Address;
        if (addr is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(addr))
        {
            return true;
        }

        // IPv4-mapped
        if (addr.IsIPv4MappedToIPv6)
        {
            return IPAddress.IsLoopback(addr.MapToIPv4());
        }

        return false;
    }

    private async Task WriteRoomAgentProfileAsync(RoomHttpResponse response, string roomId)
    {
        var profile = _profileStore.GetOrCreate(roomId);
        var skills = _skillCatalog.Skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new { name = s.Name })
            .ToArray();
        var mcp = _settings.McpServers
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new { name = s.Name })
            .ToArray();

        await WriteJsonAsync(response, HttpStatusCode.OK, new
        {
            roomId,
            profile = new
            {
                toolAllowList = profile.ToolAllowList,
                skillNames = profile.SkillNames,
                mcpServerNames = profile.McpServerNames
            },
            catalog = new
            {
                tools = RoomAgentProfileStore.CatalogTools,
                skills,
                mcpServers = mcp
            }
        }).ConfigureAwait(false);
    }

    private async Task HandleSaveRoomAgentProfileAsync(RoomHttpContext context, string roomId)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (root.TryGetProperty("profile", out var nested))
            {
                root = nested;
            }

            var profile = _profileStore.GetOrCreate(roomId);
            if (root.TryGetProperty("toolAllowList", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                profile.ToolAllowList = tools.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (root.TryGetProperty("skillNames", out var skills) && skills.ValueKind == JsonValueKind.Array)
            {
                profile.SkillNames = skills.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (root.TryGetProperty("mcpServerNames", out var mcp) && mcp.ValueKind == JsonValueKind.Array)
            {
                profile.McpServerNames = mcp.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (!profile.ToolAllowList.Any(t => string.Equals(t, "room_share_file", StringComparison.OrdinalIgnoreCase)))
            {
                profile.ToolAllowList.Add("room_share_file");
            }

            _profileStore.Save(roomId, profile);
            await WriteRoomAgentProfileAsync(context.Response, roomId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save room agent profile.");
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "invalid_profile" })
                .ConfigureAwait(false);
        }
    }

    /// <summary>Same resolution order as RoomAgentBridge: room workspace → active → first configured → null.</summary>
    private string? ResolveRoomWorkspaceRoot(string roomId)
    {
        var room = _store.GetRoom(roomId);
        if (room is not null && !string.IsNullOrWhiteSpace(room.WorkspaceId))
        {
            var match = _settings.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, room.WorkspaceId, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.IsNullOrWhiteSpace(match.RootPath))
            {
                return match.RootPath.Trim();
            }
        }

        if (room is not null && !string.IsNullOrWhiteSpace(room.WorkspaceRoot))
        {
            return room.WorkspaceRoot.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_workspaceContext.RootPath))
        {
            return _workspaceContext.RootPath;
        }

        var first = _settings.Workspaces.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w.RootPath));
        return first?.RootPath?.Trim();
    }

    private (string? Root, string? Id) ResolveWorkspaceInputs(
        string? workspaceRoot,
        string? workspaceId,
        bool allowEmpty = true)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            var match = _settings.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, workspaceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.IsNullOrWhiteSpace(match.RootPath))
            {
                return (match.RootPath.Trim(), match.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var byRoot = _settings.Workspaces.FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.RootPath)
                && string.Equals(w.RootPath.Trim(), workspaceRoot.Trim(), StringComparison.OrdinalIgnoreCase));
            return (workspaceRoot.Trim(), byRoot?.Id ?? workspaceId?.Trim());
        }

        if (!allowEmpty)
        {
            return (null, null);
        }

        if (!string.IsNullOrWhiteSpace(_workspaceContext.RootPath))
        {
            return (_workspaceContext.RootPath, _workspaceContext.WorkspaceId);
        }

        var first = _settings.Workspaces.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w.RootPath));
        if (first is not null)
        {
            return (first.RootPath.Trim(), first.Id);
        }

        return (null, null);
    }

    private string? ResolveWorkspaceName(string? workspaceId, string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            var match = _settings.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, workspaceId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return string.IsNullOrWhiteSpace(match.Name) ? match.RootPath : match.Name;
            }
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var byRoot = _settings.Workspaces.FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.RootPath)
                && string.Equals(w.RootPath.Trim(), workspaceRoot.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byRoot is not null)
            {
                return string.IsNullOrWhiteSpace(byRoot.Name) ? byRoot.RootPath : byRoot.Name;
            }

            try
            {
                return Path.GetFileName(workspaceRoot.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    is { Length: > 0 } name
                    ? name
                    : workspaceRoot;
            }
            catch
            {
                return workspaceRoot;
            }
        }

        return null;
    }

    private static string? ResolveLanIpv4()
    {
        var ips = GetLocalIpv4Addresses().ToList();
        return ips.OrderByDescending(IsPrivateIpv4).FirstOrDefault();
    }

    private static bool IsPrivateIpv4(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static IEnumerable<string> GetLocalIpv4Addresses()
    {
        var results = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = ni.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(uni.Address))
                    {
                        continue;
                    }

                    var bytes = uni.Address.GetAddressBytes();
                    // skip APIPA
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue;
                    }

                    results.Add(uni.Address.ToString());
                }
            }
        }
        catch
        {
            // ignore
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static string BuildInviteUrl(string lanIp, int port, string roomId, string token) =>
        $"http://{lanIp}:{port}/r/{roomId}?t={Uri.EscapeDataString(token)}";

    private static string? ResolveStaticRelativePath(Uri requestUri, string absolutePath)
    {
        var rawPath = Uri.UnescapeDataString(string.IsNullOrWhiteSpace(absolutePath) ? requestUri.AbsolutePath : absolutePath);
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath == "/")
        {
            return AthlonRoomAssets.EntryFileName;
        }

        var trimmed = rawPath.TrimStart('/');
        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            return null;
        }

        // SPA client routes: /r or /r/{roomId} only (not /r/assets/...).
        if (segments.Length == 1
            && segments[0].Equals("r", StringComparison.OrdinalIgnoreCase))
        {
            return AthlonRoomAssets.EntryFileName;
        }

        if (segments.Length == 2
            && segments[0].Equals("r", StringComparison.OrdinalIgnoreCase)
            && RoomStore.IsSafeId(segments[1]))
        {
            return AthlonRoomAssets.EntryFileName;
        }

        return Path.Combine(segments);
    }

    private static bool IsPathWithinRoot(string rootDirectory, string candidatePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return MimeTypes.TryGetValue(extension, out var mimeType)
            ? mimeType
            : "application/octet-stream";
    }

    private static void AddCors(RoomHttpResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, X-File-Name";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    }

    private static async Task WriteJsonAsync(RoomHttpResponse response, HttpStatusCode status, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        await WriteTextAsync(response, status, json, "application/json; charset=utf-8").ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        RoomHttpResponse response,
        HttpStatusCode status,
        string body,
        string contentType = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.WriteBodyAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static async Task WriteSseAsync(RoomHttpResponse response, string eventName, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var payload = $"event: {eventName}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await response.WriteBodyAsync(bytes).ConfigureAwait(false);
        await response.FlushAsync().ConfigureAwait(false);
    }
}
