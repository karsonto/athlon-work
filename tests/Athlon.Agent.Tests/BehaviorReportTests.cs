using System.Net;
using System.Net.Http;
using System.Text;
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
        EventManager.ResetForTests();
        _root = Path.Combine(Path.GetTempPath(), "athlon-behavior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        EventManager.ResetForTests();
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

        var em = EventManager.Instance;
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

        var em = EventManager.Instance;
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

        var settings = new AppSettings
        {
            BehaviorReport = new BehaviorReportSettings
            {
                Enabled = true,
                BaseUrl = "https://example.com"
            }
        };
        var device = new ClientDeviceInfo(appName: "Athlon Agent", appVersion: "1.0.0");
        var uploader = new BehaviorReportUploader(
            new HttpClient(new SuccessHandler()),
            settings,
            store,
            device,
            new NoOpLogger());

        var uploaded = await uploader.UploadPendingAsync();
        Assert.Equal(1, uploaded);
        Assert.False(File.Exists(store.PendingPath));
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

    private sealed class SuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
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
