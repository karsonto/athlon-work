using System.Net;
using System.Text;
using System.Text.Json;
using Athlon.Agent.App.Services.Room;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athlon.Agent.Tests;

public sealed class RoomCloudSandboxTests
{
    [Fact]
    public void RuntimeModes_Normalize_CloudSandbox()
    {
        Assert.Equal(RoomAgentRuntimeModes.CloudSandbox, RoomAgentRuntimeModes.Normalize("cloud_sandbox"));
        Assert.Equal(RoomAgentRuntimeModes.CloudSandbox, RoomAgentRuntimeModes.Normalize("cloud"));
        Assert.True(RoomAgentRuntimeModes.IsCloudSandbox("cloud_sandbox"));
        Assert.True(RoomAgentRuntimeModes.IsSandbox("cloud_sandbox"));
        Assert.False(RoomAgentRuntimeModes.IsLocalDockerSandbox("cloud_sandbox"));
        Assert.Equal("云沙箱", RoomAgentRuntimeModes.DisplayLabel("cloud_sandbox"));
    }

    [Fact]
    public async Task Probe_WithoutBaseUrl_IsUnavailable()
    {
        var settings = new AppSettings();
        var sut = CreateSut(settings, new FakeSsoStore("tok"), new ScriptHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)));

        var probe = await sut.ProbeAsync();
        Assert.False(probe.Available);
        Assert.Contains("BaseUrl", probe.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_WithoutApiKey_IsUnavailable()
    {
        var settings = new AppSettings
        {
            CloudSandbox = new CloudSandboxSettings { BaseUrl = "http://127.0.0.1:9" }
        };
        var sut = CreateSut(settings, new FakeSsoStore(null), new ScriptHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)));

        var probe = await sut.ProbeAsync();
        Assert.False(probe.Available);
        Assert.Contains("ApiKey", probe.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ensure_Exec_Pull_Dispose_Contract()
    {
        using var workspace = new TempDirectoryScope("cloud-ws");
        File.WriteAllText(Path.Combine(workspace.Root, "hello.txt"), "from-host");
        using var skills = new TempDirectoryScope("cloud-skills");
        Directory.CreateDirectory(Path.Combine(skills.Root, "demo-skill"));
        File.WriteAllText(Path.Combine(skills.Root, "demo-skill", "SKILL.md"), "# demo");

        var calls = new List<string>();
        string? sessionId = null;
        var handler = new ScriptHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            calls.Add($"{req.Method.Method} {path}");

            if (path == "/v1/status" && req.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, new { available = true, message = "ok" });
            }

            if (path == "/v1/sessions" && req.Method == HttpMethod.Post)
            {
                sessionId = "sess-1";
                return Json(HttpStatusCode.OK, new { sessionId });
            }

            if (path.Contains("/sync", StringComparison.Ordinal) && req.Method == HttpMethod.Post)
            {
                Assert.NotNull(req.Content);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/sync/pull", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                // Return a tiny gzip tar that writes pull.txt
                var ms = CloudWorkspaceSync.PackDirectoryTarGz(CreatePullSource());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(ms)
                };
            }

            if (path.EndsWith("/exec", StringComparison.Ordinal) && req.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.OK, new
                {
                    exitCode = 0,
                    stdout = "pong",
                    stderr = ""
                });
            }

            if (req.Method == HttpMethod.Delete && path.StartsWith("/v1/sessions/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected {req.Method} {path}")
            };
        });

        var settings = new AppSettings
        {
            CloudSandbox = new CloudSandboxSettings
            {
                BaseUrl = "http://cloud.test",
                ApiKey = "test-key",
                ImageTag = "athlon-room-sandbox:2",
                PreferIncrementalSync = false
            }
        };
        var sut = CreateSut(settings, new FakeSsoStore("sso-token"), handler);

        await sut.EnsureRoomSessionAsync(
            "room-a",
            workspace.Root,
            skills.Root,
            skillNames: ["demo-skill"]);

        Assert.Equal("sess-1", sessionId);
        Assert.Contains(calls, c => c.StartsWith("POST /v1/sessions", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Contains("/sync", StringComparison.Ordinal));

        var exec = await sut.ExecAsync("room-a", "echo pong", "/workspace", 30);
        Assert.Equal(0, exec.ExitCode);
        Assert.Equal("pong", exec.Stdout);

        await sut.PullWorkspaceAsync("room-a", workspace.Root);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "pull.txt")));

        await sut.DisposeRoomAsync("room-a");
        Assert.Contains(calls, c => c == "DELETE /v1/sessions/sess-1");

        // Second dispose is no-op (no extra delete required).
        var deleteCount = calls.Count(c => c.StartsWith("DELETE ", StringComparison.Ordinal));
        await sut.DisposeRoomAsync("room-a");
        Assert.Equal(deleteCount, calls.Count(c => c.StartsWith("DELETE ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Ensure_QuotaFailure_DoesNotFallback()
    {
        var handler = new ScriptHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/v1/status")
            {
                return Json(HttpStatusCode.OK, new { available = true });
            }

            return new HttpResponseMessage(HttpStatusCode.PaymentRequired)
            {
                Content = new StringContent("quota exceeded")
            };
        });

        using var workspace = new TempDirectoryScope("cloud-ws-q");
        using var skills = new TempDirectoryScope("cloud-skills-q");
        var settings = new AppSettings
        {
            CloudSandbox = new CloudSandboxSettings { BaseUrl = "http://cloud.test", ApiKey = "test-key", PreferIncrementalSync = false }
        };
        var sut = CreateSut(settings, new FakeSsoStore("tok"), handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EnsureRoomSessionAsync("r1", workspace.Root, skills.Root));
        Assert.Contains("配额", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackDirectory_IgnoresNodeModules()
    {
        using var dir = new TempDirectoryScope("pack");
        File.WriteAllText(Path.Combine(dir.Root, "keep.txt"), "x");
        Directory.CreateDirectory(Path.Combine(dir.Root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(dir.Root, "node_modules", "pkg", "index.js"), "y");

        using var tar = CloudWorkspaceSync.PackDirectoryTarGz(dir.Root);
        using var unpack = new TempDirectoryScope("unpack");
        CloudWorkspaceSync.UnpackTarGzToDirectory(tar, unpack.Root);

        Assert.True(File.Exists(Path.Combine(unpack.Root, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(unpack.Root, "node_modules")));
    }

    [Fact]
    public void BackendResolver_SelectsByMode()
    {
        var docker = new RoomDockerSandbox(NullLogger<RoomDockerSandbox>.Instance);
        var cloud = CreateSut(
            new AppSettings { CloudSandbox = new CloudSandboxSettings { BaseUrl = "http://x", ApiKey = "k" } },
            new FakeSsoStore("t"),
            new ScriptHandler(_ => Json(HttpStatusCode.OK, new { available = true })));
        var resolver = new RoomSandboxBackendResolver(docker, cloud);

        Assert.Same(docker, resolver.Resolve(RoomAgentRuntimeModes.Sandbox));
        Assert.Same(cloud, resolver.Resolve(RoomAgentRuntimeModes.CloudSandbox));
        Assert.Null(resolver.Resolve(RoomAgentRuntimeModes.Local));
    }

    private static string CreatePullSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-pull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pull.txt"), "from-cloud");
        return root;
    }

    private static RoomCloudSandbox CreateSut(
        AppSettings settings,
        IImpSsoSessionStore sso,
        HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new RoomCloudSandbox(settings, NullLogger<RoomCloudSandbox>.Instance, http);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class ScriptHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class FakeSsoStore(string? token) : IImpSsoSessionStore
    {
        public ImpSsoSession? GetCachedSession() =>
            string.IsNullOrWhiteSpace(token)
                ? null
                : new ImpSsoSession
                {
                    SsoToken = token!,
                    UserId = "u1",
                    DisplayName = "Test",
                    LoggedInAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                };

        public void SaveSession(ImpSsoSession session)
        {
        }

        public void Clear()
        {
        }

        public bool IsExpired(ImpSsoSession session) => session.IsExpired(DateTimeOffset.UtcNow);
    }
}
