using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Athlon.Agent.App.Services.Room;

public sealed class RoomDockerSandbox : IRoomDockerSandbox, IAsyncDisposable
{
    /// <summary>Bump tag when Dockerfile/runtime contents change so EnsureReady rebuilds.</summary>
    public const string ImageName = "athlon-room-sandbox:2";

    private readonly ILogger<RoomDockerSandbox> _logger;
    private readonly SemaphoreSlim _readyGate = new(1, 1);
    private readonly SemaphoreSlim _containerGate = new(1, 1);
    private readonly ConcurrentDictionary<string, RoomContainerState> _containers =
        new(StringComparer.OrdinalIgnoreCase);

    private RoomDockerEngineClient? _client;
    private bool _imageReady;

    public RoomDockerSandbox(ILogger<RoomDockerSandbox> logger)
    {
        _logger = logger;
    }

    public async Task<RoomSandboxProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetClient();
            if (!await client.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                return new RoomSandboxProbeResult
                {
                    Available = false,
                    ImageReady = false,
                    Message = "Docker daemon 未响应。请安装并启动 Docker Desktop。"
                };
            }

            var version = await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            var imageReady = await client.ImageExistsAsync(ImageName, cancellationToken).ConfigureAwait(false);
            _imageReady = imageReady;
            return new RoomSandboxProbeResult
            {
                Available = true,
                ImageReady = imageReady,
                DockerVersion = version,
                Message = imageReady
                    ? "Docker 已就绪（含 Python 3.12 / Java 8 / Node 24 / Chromium）。"
                    : "Docker 已连接；首次使用 Sandbox 时将构建镜像。"
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker probe failed.");
            return new RoomSandboxProbeResult
            {
                Available = false,
                ImageReady = false,
                Message = "无法连接 Docker。请安装并启动 Docker Desktop。"
            };
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_imageReady)
        {
            return;
        }

        await _readyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_imageReady)
            {
                return;
            }

            var client = GetClient();
            if (!await client.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Docker daemon 未就绪。请启动 Docker Desktop。");
            }

            if (await client.ImageExistsAsync(ImageName, cancellationToken).ConfigureAwait(false))
            {
                _imageReady = true;
                return;
            }

            _logger.LogInformation("Building sandbox image {Image}…", ImageName);
            var contextFiles = ResolveBuildContextFiles();
            await using var tar = RoomDockerTar.CreateBuildContextTar(contextFiles);
            await client.BuildImageAsync(tar, ImageName, "Dockerfile", cancellationToken).ConfigureAwait(false);
            _imageReady = true;
        }
        finally
        {
            _readyGate.Release();
        }
    }

    public Task EnsureRoomSessionAsync(
        string roomId,
        string hostWorkspaceRoot,
        string hostSkillsRoot,
        IReadOnlyList<string>? skillNames = null,
        CancellationToken cancellationToken = default) =>
        EnsureRoomContainerAsync(roomId, hostWorkspaceRoot, hostSkillsRoot, cancellationToken);

    public Task PullWorkspaceAsync(
        string roomId,
        string hostWorkspaceRoot,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task EnsureRoomContainerAsync(
        string roomId,
        string hostWorkspaceRoot,
        string hostSkillsRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        var ws = RoomSandboxPathMap.NormalizeHostRoot(hostWorkspaceRoot)
            ?? throw new InvalidOperationException("Sandbox 需要有效的房间工作区路径。");
        var skills = RoomSandboxPathMap.NormalizeHostRoot(hostSkillsRoot)
            ?? throw new InvalidOperationException("Skills 目录不可用。");

        if (!Directory.Exists(ws))
        {
            throw new InvalidOperationException($"工作区不存在: {ws}");
        }

        Directory.CreateDirectory(skills);

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await _containerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_containers.TryGetValue(roomId, out var existing)
                && string.Equals(existing.WorkspaceRoot, ws, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.SkillsRoot, skills, StringComparison.OrdinalIgnoreCase))
            {
                var client = GetClient();
                if (await client.IsContainerRunningAsync(existing.ContainerId, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await client.StartContainerAsync(existing.ContainerId, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_containers.TryRemove(roomId, out var stale))
            {
                await RemoveContainerQuietAsync(stale.ContainerId, cancellationToken).ConfigureAwait(false);
            }

            var client2 = GetClient();
            var name = ContainerName(roomId);
            var previous = await client2.FindContainerIdByNameAsync(name, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                await RemoveContainerQuietAsync(previous, cancellationToken).ConfigureAwait(false);
            }

            var binds = new[]
            {
                $"{ToDockerBindSource(ws)}:{RoomSandboxPathMap.ContainerWorkspaceRoot}:rw",
                $"{ToDockerBindSource(skills)}:{RoomSandboxPathMap.ContainerSkillsRoot}:ro"
            };

            var createBody = new Dictionary<string, object?>
            {
                ["Image"] = ImageName,
                ["User"] = "athlon",
                ["WorkingDir"] = RoomSandboxPathMap.ContainerWorkspaceRoot,
                ["Cmd"] = new[] { "sleep", "infinity" },
                ["HostConfig"] = new Dictionary<string, object?>
                {
                    ["Binds"] = binds,
                    // bridge: browser_navigate / Chromium need outbound network
                    ["NetworkMode"] = "bridge",
                    ["ReadonlyRootfs"] = true,
                    ["CapDrop"] = new[] { "ALL" },
                    ["SecurityOpt"] = new[] { "no-new-privileges:true" },
                    ["NanoCpus"] = 2_000_000_000L,
                    ["Memory"] = 3L * 1024 * 1024 * 1024,
                    ["ShmSize"] = 256L * 1024 * 1024,
                    ["Tmpfs"] = new Dictionary<string, string>
                    {
                        ["/tmp"] = "rw,nosuid,size=512m",
                        ["/tmp/athlon"] = "rw,nosuid,size=256m",
                        ["/tmp/athlon-home"] = "rw,nosuid,size=64m",
                        ["/tmp/athlon-chrome"] = "rw,nosuid,size=256m"
                    }
                },
                ["Env"] = new[]
                {
                    "HOME=/tmp/athlon-home",
                    "JAVA_HOME=/opt/java/openjdk",
                    "LANG=C.UTF-8",
                    "TZ=Asia/Shanghai",
                    "ATHLON_CHROME_BIN=chromium",
                    "ATHLON_CDP=http://127.0.0.1:9222",
                    "ATHLON_CHROME_PROFILE=/tmp/athlon-chrome"
                }
            };

            var id = await client2.CreateContainerAsync(createBody, cancellationToken, name)
                .ConfigureAwait(false);
            await client2.StartContainerAsync(id, cancellationToken).ConfigureAwait(false);
            _containers[roomId] = new RoomContainerState(id, ws, skills);
            _logger.LogInformation("Sandbox container {ContainerId} ready for room {RoomId}", id, roomId);
        }
        finally
        {
            _containerGate.Release();
        }
    }

    public async Task DisposeRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return;
        }

        if (!_containers.TryRemove(roomId, out var state))
        {
            try
            {
                var client = GetClient();
                var id = await client.FindContainerIdByNameAsync(ContainerName(roomId), cancellationToken)
                    .ConfigureAwait(false);
                if (id is not null)
                {
                    await RemoveContainerQuietAsync(id, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DisposeRoom lookup failed for {RoomId}", roomId);
            }

            return;
        }

        await RemoveContainerQuietAsync(state.ContainerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomSandboxExecResult> ExecAsync(
        string roomId,
        string command,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var id = RequireContainer(roomId);
        var client = GetClient();
        var cwd = string.IsNullOrWhiteSpace(workingDirectory)
            ? RoomSandboxPathMap.ContainerWorkspaceRoot
            : workingDirectory;
        var (exit, stdout, stderr) = await client
            .ExecAsync(id, ["bash", "-lc", command], cwd, timeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        return new RoomSandboxExecResult
        {
            ExitCode = exit,
            Stdout = stdout,
            Stderr = stderr
        };
    }

    public async Task<byte[]?> ReadFileAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default)
    {
        var id = RequireContainer(roomId);
        var client = GetClient();
        var tar = await client.GetArchiveAsync(id, containerPath, cancellationToken).ConfigureAwait(false);
        return tar is null ? null : RoomDockerTar.ExtractFirstFileFromTar(tar);
    }

    public async Task WriteFileAsync(
        string roomId,
        string containerPath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var id = RequireContainer(roomId);
        var client = GetClient();
        var dir = UnixDirName(containerPath);
        var fileName = UnixFileName(containerPath);
        await EnsureDirectoryAsync(roomId, dir, cancellationToken).ConfigureAwait(false);
        await using var tar = RoomDockerTar.CreateSingleFileTar(fileName, content);
        await client.PutArchiveAsync(id, dir, tar, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureDirectoryAsync(
        string roomId,
        string containerDirectory,
        CancellationToken cancellationToken = default)
    {
        var id = RequireContainer(roomId);
        var client = GetClient();
        var dir = string.IsNullOrWhiteSpace(containerDirectory)
            ? RoomSandboxPathMap.ContainerWorkspaceRoot
            : containerDirectory.TrimEnd('/');
        var result = await client
            .ExecAsync(id, ["bash", "-lc", $"mkdir -p -- {ShellQuote(dir)}"], "/", 30, cancellationToken)
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
        var id = RequireContainer(roomId);
        var client = GetClient();
        var result = await client
            .ExecAsync(
                id,
                ["bash", "-lc", $"test -e {ShellQuote(containerPath)}"],
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
        var id = RequireContainer(roomId);
        var client = GetClient();
        var result = await client
            .ExecAsync(
                id,
                ["bash", "-lc", $"test -d {ShellQuote(containerPath)}"],
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
        var id = RequireContainer(roomId);
        var client = GetClient();
        var result = await client
            .ExecAsync(
                id,
                ["bash", "-lc", $"ls -1A -- {ShellQuote(containerPath)}"],
                "/",
                30,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Stderr) ? "list failed" : result.Stderr);
        }

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var roomId in _containers.Keys.ToArray())
        {
            try
            {
                await DisposeRoomAsync(roomId).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _readyGate.Dispose();
        _containerGate.Dispose();
    }

    private string RequireContainer(string roomId)
    {
        if (_containers.TryGetValue(roomId, out var state))
        {
            return state.ContainerId;
        }

        throw new InvalidOperationException("Sandbox 容器尚未启动。请先确保房间工作区已配置并重试 @agent。");
    }

    private RoomDockerEngineClient GetClient() =>
        _client ??= RoomDockerEngineClient.Create();

    private async Task RemoveContainerQuietAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            var client = GetClient();
            await client.StopContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
            await client.RemoveContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove container {ContainerId}", containerId);
        }
    }

    private static string ContainerName(string roomId) =>
        "athlon-room-" + new string(roomId.Where(char.IsLetterOrDigit).Take(24).ToArray()).ToLowerInvariant();

    private static string ToDockerBindSource(string hostPath)
    {
        // Docker Desktop on Windows accepts Windows paths; normalize to full path.
        return Path.GetFullPath(hostPath);
    }

    private static string UnixDirName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var idx = normalized.LastIndexOf('/');
        return idx <= 0 ? "/" : normalized[..idx];
    }

    private static string UnixFileName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var idx = normalized.LastIndexOf('/');
        return idx < 0 ? normalized : normalized[(idx + 1)..];
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static IReadOnlyDictionary<string, byte[]> ResolveBuildContextFiles()
    {
        var dirCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "docker", "athlon-room-sandbox"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "docker", "athlon-room-sandbox")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docker", "athlon-room-sandbox"))
        };

        foreach (var dir in dirCandidates)
        {
            var dockerfile = Path.Combine(dir, "Dockerfile");
            var browserCtl = Path.Combine(dir, "browser_ctl.py");
            if (File.Exists(dockerfile) && File.Exists(browserCtl))
            {
                return new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["Dockerfile"] = File.ReadAllBytes(dockerfile),
                    ["browser_ctl.py"] = File.ReadAllBytes(browserCtl)
                };
            }
        }

        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Dockerfile"] = Encoding.UTF8.GetBytes(EmbeddedDockerfile),
            ["browser_ctl.py"] = Encoding.UTF8.GetBytes(EmbeddedBrowserCtl)
        };
    }

    private const string EmbeddedDockerfile = """
        FROM python:3.12-slim-bookworm
        ENV DEBIAN_FRONTEND=noninteractive \
            JAVA_HOME=/opt/java/openjdk \
            PATH="/opt/java/openjdk/bin:/usr/local/bin:${PATH}" \
            ATHLON_CHROME_BIN=chromium \
            ATHLON_CDP=http://127.0.0.1:9222 \
            ATHLON_CHROME_PROFILE=/tmp/athlon-chrome \
            HOME=/tmp/athlon-home
        RUN apt-get update \
            && apt-get install -y --no-install-recommends \
                ca-certificates curl gnupg git bash coreutils findutils grep sed gawk xz-utils \
                chromium fonts-liberation fonts-noto-core \
            && curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
            && apt-get install -y --no-install-recommends nodejs \
            && mkdir -p /opt/java /opt/athlon \
            && curl -fsSL "https://github.com/adoptium/temurin8-binaries/releases/download/jdk8u432-b06/OpenJDK8U-jdk_x64_linux_hotspot_8u432b06.tar.gz" \
                | tar -xz -C /opt/java \
            && ln -s /opt/java/jdk8u432-b06 /opt/java/openjdk \
            && pip install --no-cache-dir numpy pandas openpyxl python-docx Pillow websocket-client \
            && useradd --create-home --uid 10001 --shell /bin/bash athlon \
            && mkdir -p /workspace /skills /tmp/athlon /tmp/athlon-home /tmp/athlon-chrome \
            && chown -R athlon:athlon /workspace /tmp/athlon /tmp/athlon-home /tmp/athlon-chrome /opt/athlon \
            && chmod 755 /skills \
            && ln -sf /usr/local/bin/python3 /usr/local/bin/python \
            && rm -rf /var/lib/apt/lists/*
        COPY browser_ctl.py /opt/athlon/browser_ctl.py
        RUN chmod 755 /opt/athlon/browser_ctl.py && chown athlon:athlon /opt/athlon/browser_ctl.py
        WORKDIR /workspace
        USER athlon
        CMD ["sleep", "infinity"]
        """;

    // Keep in sync with docker/athlon-room-sandbox/browser_ctl.py (fallback when file missing).
    private const string EmbeddedBrowserCtl = """
        #!/usr/bin/env python3
        import json, os, subprocess, sys, time, urllib.error, urllib.parse, urllib.request
        CDP = os.environ.get("ATHLON_CDP", "http://127.0.0.1:9222")
        USER_DATA = os.environ.get("ATHLON_CHROME_PROFILE", "/tmp/athlon-chrome")
        CHROME_BIN = os.environ.get("ATHLON_CHROME_BIN", "chromium")
        def _get(path, timeout=3.0):
            with urllib.request.urlopen(CDP + path, timeout=timeout) as resp:
                return json.loads(resp.read().decode("utf-8"))
        def _put(path, timeout=30.0):
            req = urllib.request.Request(CDP + path, method="PUT")
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return json.loads(resp.read().decode("utf-8"))
        def ensure():
            try:
                _get("/json/version", timeout=1.0); return
            except Exception:
                pass
            os.makedirs(USER_DATA, exist_ok=True)
            log_path = os.path.join(USER_DATA, "chrome.log")
            with open(log_path, "ab") as log:
                subprocess.Popen([CHROME_BIN, "--headless=new", "--remote-debugging-port=9222",
                    "--remote-allow-origins=*", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
                    f"--user-data-dir={USER_DATA}", "about:blank"], stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            deadline = time.time() + 20
            while time.time() < deadline:
                try:
                    _get("/json/version", timeout=1.0); return
                except Exception:
                    time.sleep(0.25)
            raise SystemExit("failed to start chromium CDP on :9222")
        def list_pages():
            return [p for p in _get("/json/list") if p.get("type") == "page"]
        def active_page():
            pages = list_pages(); return pages[0] if pages else None
        def navigate(url):
            ensure()
            quoted = urllib.parse.quote(url, safe=":/?#[]@!$&'()*+,;=%")
            try:
                page = _put(f"/json/new?{quoted}", timeout=60.0)
            except urllib.error.HTTPError:
                page = _get(f"/json/new?{quoted}", timeout=60.0)
            time.sleep(0.6)
            refreshed = active_page() or page
            return {"ok": True, "action": "navigate", "url": refreshed.get("url") or url, "title": refreshed.get("title") or "", "id": refreshed.get("id")}
        def _cdp_ws(method, params=None):
            import websocket
            page = active_page()
            if page is None: raise SystemExit("no active page")
            ws_url = page.get("webSocketDebuggerUrl")
            if not ws_url: raise SystemExit("page has no webSocketDebuggerUrl")
            ws = websocket.create_connection(ws_url, timeout=20)
            try:
                msg_id = 1
                ws.send(json.dumps({"id": msg_id, "method": method, "params": params or {}}))
                while True:
                    data = json.loads(ws.recv())
                    if data.get("id") == msg_id:
                        if "error" in data: raise SystemExit(json.dumps(data["error"]))
                        return data.get("result") or {}
            finally:
                ws.close()
        def go_back():
            ensure(); _cdp_ws("Page.goBack"); time.sleep(0.4); page = active_page() or {}
            return {"ok": True, "action": "back", "url": page.get("url", ""), "title": page.get("title", "")}
        def go_forward():
            ensure(); _cdp_ws("Page.goForward"); time.sleep(0.4); page = active_page() or {}
            return {"ok": True, "action": "forward", "url": page.get("url", ""), "title": page.get("title", "")}
        def reload():
            ensure(); _cdp_ws("Page.reload", {"ignoreCache": False}); time.sleep(0.5); page = active_page() or {}
            return {"ok": True, "action": "reload", "url": page.get("url", ""), "title": page.get("title", "")}
        def info():
            ensure(); page = active_page() or {}
            return {"ok": True, "url": page.get("url", ""), "title": page.get("title", "")}
        def main(argv):
            if len(argv) < 2: return 2
            cmd = argv[1].lower()
            try:
                if cmd == "ensure": ensure(); result = {"ok": True}
                elif cmd == "info": result = info()
                elif cmd == "navigate":
                    if len(argv) < 3: raise SystemExit("navigate requires url")
                    result = navigate(argv[2])
                elif cmd == "back": result = go_back()
                elif cmd == "forward": result = go_forward()
                elif cmd in ("reload", "refresh"): result = reload()
                else: raise SystemExit(f"unknown command: {cmd}")
                print(json.dumps(result, ensure_ascii=False)); return 0
            except SystemExit as exc:
                print(json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False)); return 1
            except Exception as exc:
                print(json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False)); return 1
        if __name__ == "__main__":
            raise SystemExit(main(sys.argv))
        """;

    private sealed record RoomContainerState(string ContainerId, string WorkspaceRoot, string SkillsRoot);
}
