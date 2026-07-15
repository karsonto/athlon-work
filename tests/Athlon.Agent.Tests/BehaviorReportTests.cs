using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.BehaviorReport;

namespace Athlon.Agent.Tests;

[Collection(nameof(BehaviorReportCollection))]
public sealed class BehaviorReportTests : IDisposable
{
    private readonly string _root;

    public BehaviorReportTests()
    {
        BehaviorEventManager.ResetForTests();
        _root = Path.Combine(Path.GetTempPath(), "athlon-behavior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        BehaviorEventManager.ResetForTests();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup races
        }
    }

    [Fact]
    public void AttemptMapper_MapsModelAndTools()
    {
        var model = BehaviorAttemptEventMapper.Map(new AgentAttemptEvent(
            DateTimeOffset.UtcNow, "a1", "s1", "t1",
            AgentAttemptKind.Model, ModelCallPurpose.Chat, null, null, "gpt",
            10, 5, "success", null, 100));
        Assert.NotNull(model);
        Assert.Equal(BehaviorEventIds.ModelCall, model!.Value.EventId);

        var mcp = BehaviorAttemptEventMapper.Map(new AgentAttemptEvent(
            DateTimeOffset.UtcNow, "a2", "s1", "t1",
            AgentAttemptKind.Tool, ModelCallPurpose.Chat, "mcp_server__tool", null, null,
            0, 0, "success", null, 50));
        Assert.NotNull(mcp);
        Assert.Equal(BehaviorEventIds.McpTool, mcp!.Value.EventId);
        Assert.Equal("server", mcp.Value.Parameters["server_name"]);

        var gateway = BehaviorAttemptEventMapper.Map(new AgentAttemptEvent(
            DateTimeOffset.UtcNow, "a3", "s1", "t1",
            AgentAttemptKind.Tool, ModelCallPurpose.Chat, "mcp_search", null, null,
            0, 0, "success", null, 20));
        Assert.NotNull(gateway);
        Assert.Equal(BehaviorEventIds.McpTool, gateway!.Value.EventId);
        Assert.Equal("search", gateway.Value.Parameters["mode"]);

        var skillSkip = BehaviorAttemptEventMapper.Map(new AgentAttemptEvent(
            DateTimeOffset.UtcNow, "a4", "s1", "t1",
            AgentAttemptKind.Tool, ModelCallPurpose.Chat, "load_skill_through_path", null, null,
            0, 0, "success", null, 10));
        Assert.Null(skillSkip);

        var local = BehaviorAttemptEventMapper.Map(new AgentAttemptEvent(
            DateTimeOffset.UtcNow, "a5", "s1", "t1",
            AgentAttemptKind.Tool, ModelCallPurpose.Chat, "file_read", null, null,
            0, 0, "success", null, 10));
        Assert.NotNull(local);
        Assert.Equal(BehaviorEventIds.ToolInvoke, local!.Value.EventId);
    }

    [Fact]
    public async Task EventManager_Disabled_DoesNotWritePendingFile()
    {
        var paths = new TestPaths(_root);
        paths.EnsureCreated();
        var settings = new AppSettings
        {
            BehaviorReport = new BehaviorReportSettings { Enabled = false, BaseUrl = "https://example.com" }
        };

        var em = BehaviorEventManager.Instance;
        em.Configure(settings, paths, new HttpClient(), new NoOpLogger());
        em.Start();
        em.Record(BehaviorEventIds.AppStart, BehaviorEventTypes.Event, BehaviorEventIds.AppStart);
        await Task.Delay(100);
        em.Stop();

        var pending = Path.Combine(paths.BehaviorPath, "pending.jsonl");
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public async Task EventManager_Enabled_WritesPendingFile()
    {
        var paths = new TestPaths(_root);
        paths.EnsureCreated();
        var settings = new AppSettings
        {
            BehaviorReport = new BehaviorReportSettings
            {
                Enabled = true,
                BaseUrl = "https://example.com",
                UploadIntervalMinutes = 60
            }
        };

        var em = BehaviorEventManager.Instance;
        em.Configure(settings, paths, new HttpClient(new FailHandler()), new NoOpLogger());
        em.Start();
        em.Record(
            BehaviorEventIds.ModelCall,
            BehaviorEventTypes.Action,
            BehaviorEventIds.ModelCall,
            new Dictionary<string, object?> { ["purpose"] = "Chat", ["prompt_tokens"] = 1 });

        var pending = Path.Combine(paths.BehaviorPath, "pending.jsonl");
        for (var i = 0; i < 40 && !File.Exists(pending); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(pending));
        var text = await File.ReadAllTextAsync(pending);
        Assert.Contains("model_call", text, StringComparison.Ordinal);
        em.Stop();
    }

    [Fact]
    public void BuildRequestBody_UsesEventsArrayAndEventTime()
    {
        var device = new ClientDeviceSnapshot
        {
            UserId = "000000001",
            ClientIp = "192.168.1.100",
            MacAddress = "00:1A:2B:3C:4D:5E",
            OsVersion = "Windows 10",
            AppName = "Athlon Agent",
            AppVersion = "1.0.0",
            ScreenResolution = "1920x1080"
        };
        var timestamp = new DateTimeOffset(2026, 7, 14, 10, 30, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            new BehaviorEvent
            {
                EventId = BehaviorEventIds.UserLogin,
                EventType = BehaviorEventTypes.Event,
                MessageContent = "User logged in",
                Parameters = new Dictionary<string, object?> { ["action"] = "login" },
                Timestamp = timestamp
            },
            new BehaviorEvent
            {
                EventId = BehaviorEventIds.McpTool,
                EventType = BehaviorEventTypes.Action,
                MessageContent = "User queried financial data",
                Parameters = new Dictionary<string, object?>
                {
                    ["action"] = "query",
                    ["target"] = "finance"
                },
                Timestamp = timestamp.AddSeconds(5)
            }
        };

        var body = BehaviorReportUploader.BuildRequestBody(device, events);
        var json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("000000001", root.GetProperty("user_id").GetString());
        Assert.Equal("192.168.1.100", root.GetProperty("client_ip").GetString());
        Assert.True(root.TryGetProperty("events", out var eventsEl));
        Assert.Equal(JsonValueKind.Array, eventsEl.ValueKind);
        Assert.Equal(2, eventsEl.GetArrayLength());

        var first = eventsEl[0];
        Assert.Equal(BehaviorEventIds.UserLogin, first.GetProperty("event_type").GetString());
        Assert.Equal("User logged in", first.GetProperty("message_content").GetString());
        Assert.Equal("event", first.GetProperty("event_params").GetProperty("event_kind").GetString());
        Assert.Equal("login", first.GetProperty("event_params").GetProperty("action").GetString());
        // UTC 10:30 → China 18:30
        Assert.Equal("2026-07-14 18:30:00.000", first.GetProperty("event_time").GetString());
        Assert.Equal(
            BehaviorReportUploader.FormatEventTime(timestamp),
            first.GetProperty("event_time").GetString());

        var second = eventsEl[1];
        Assert.Equal(BehaviorEventIds.McpTool, second.GetProperty("event_type").GetString());
        Assert.Equal("action", second.GetProperty("event_params").GetProperty("event_kind").GetString());
        Assert.False(root.TryGetProperty("event_type", out _));
    }

    [Fact]
    public async Task Uploader_Success_RemovesPendingEvents()
    {
        var paths = new TestPaths(_root);
        paths.EnsureCreated();
        var store = new BehaviorEventLocalStore(paths);
        await store.AppendAsync(new BehaviorEvent
        {
            EventId = BehaviorEventIds.AppStart,
            EventType = BehaviorEventTypes.Event,
            MessageContent = BehaviorEventIds.AppStart
        });
        await store.AppendAsync(new BehaviorEvent
        {
            EventId = BehaviorEventIds.UserLogin,
            EventType = BehaviorEventTypes.Event,
            MessageContent = BehaviorEventIds.UserLogin
        });

        var settings = new AppSettings
        {
            BehaviorReport = new BehaviorReportSettings
            {
                Enabled = true,
                BaseUrl = "https://example.com"
            }
        };
        var capturing = new CapturingHandler();
        var device = new ClientDeviceInfo(appName: "Athlon Agent", appVersion: "1.0.0");
        var uploader = new BehaviorReportUploader(
            new HttpClient(capturing),
            settings,
            store,
            device,
            new NoOpLogger());

        var uploaded = await uploader.UploadPendingAsync();
        Assert.Equal(2, uploaded);
        Assert.False(File.Exists(store.PendingPath));
        Assert.Equal(1, capturing.RequestCount);
        Assert.Contains("/agent/report", capturing.LastRequestUri, StringComparison.Ordinal);
        Assert.Contains("\"events\":", capturing.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"event_time\":", capturing.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploader_Failure_KeepsPendingEvents()
    {
        var paths = new TestPaths(_root);
        paths.EnsureCreated();
        var store = new BehaviorEventLocalStore(paths);
        await store.AppendAsync(new BehaviorEvent
        {
            EventId = BehaviorEventIds.AppStart,
            EventType = BehaviorEventTypes.Event,
            MessageContent = BehaviorEventIds.AppStart
        });

        var settings = new AppSettings
        {
            BehaviorReport = new BehaviorReportSettings
            {
                Enabled = true,
                BaseUrl = "https://example.com"
            }
        };
        var uploader = new BehaviorReportUploader(
            new HttpClient(new FailHandler()),
            settings,
            store,
            new ClientDeviceInfo(),
            new NoOpLogger());

        var uploaded = await uploader.UploadPendingAsync();
        Assert.Equal(0, uploaded);
        Assert.True(File.Exists(store.PendingPath));
        var remaining = await store.ReadAllAsync();
        Assert.Single(remaining);
    }

    private sealed class TestPaths(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");
        public string BehaviorPath => Path.Combine(RootPath, "behavior");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(BehaviorPath);
        }

        public string ResolveSkillPath(string path) => path;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string LastRequestUri { get; private set; } = "";
        public string LastRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString() ?? "";
            LastRequestBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public IAppLogger ForContext(string sourceContext) => this;
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
    }
}

[CollectionDefinition(nameof(BehaviorReportCollection), DisableParallelization = true)]
public sealed class BehaviorReportCollection;
