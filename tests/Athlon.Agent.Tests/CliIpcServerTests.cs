using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Cli;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Cli;

namespace Athlon.Agent.Tests;

[Trait("Category", TestCategories.Integration)]
[Trait("Category", TestCategories.UsesHttp)]
public sealed class CliIpcServerTests
{
    [Fact]
    public async Task Health_RequiresBearerToken()
    {
        await using var harness = await CliIpcHarness.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var unauthorized = await http.GetAsync(harness.Url + "v1/health");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", harness.Token);
        using var ok = await http.GetAsync(harness.Url + "v1/health");
        Assert.True(ok.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Turn_StreamsTextAndToolEvents_ThenDone()
    {
        await using var harness = await CliIpcHarness.StartAsync(orchestrator: new StubOrchestrator
        {
            Handler = async (session, _, callbacks, _) =>
            {
                if (callbacks?.OnStreamEvent is { } onEvent)
                {
                    await onEvent(new AgentStreamEvent.TextMessageContent("m1", "hello "));
                    await onEvent(new AgentStreamEvent.ToolCallStart("c1", "file_read", 0));
                    await onEvent(new AgentStreamEvent.ToolCallOutput("c1", "pom.xml\n"));
                    await onEvent(new AgentStreamEvent.ToolCallEnd("c1"));
                    await onEvent(new AgentStreamEvent.TextMessageContent("m1", "world"));
                }

                return session;
            }
        });

        var events = await harness.PostTurnAsync(harness.Cwd, "hi");
        Assert.Contains(events, e => e.Event == CliSseEventNames.Session);
        Assert.Contains(events, e => e.Event == CliSseEventNames.Text && e.Data.Contains("hello", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Event == CliSseEventNames.ToolStart && e.Data.Contains("file_read", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Event == CliSseEventNames.ToolOutput);
        Assert.Contains(events, e => e.Event == CliSseEventNames.Done);
        Assert.NotNull(harness.Storage.LastSaved);
        Assert.Equal(harness.Cwd, CliPaths.NormalizeLocalPath(harness.Storage.LastSaved!.ActiveWorkspace!));
    }

    [Fact]
    public async Task Turn_ApprovalRoundTrip()
    {
        await using var harness = await CliIpcHarness.StartAsync(orchestrator: new StubOrchestrator
        {
            Handler = async (session, _, callbacks, cancellationToken) =>
            {
                Assert.NotNull(callbacks?.OnToolApprovalRequested);
                var pending = new PendingToolApproval(
                    "call-1",
                    "file_write",
                    ToolCallArguments.FromStrings(new Dictionary<string, string> { ["path"] = "a.txt" }),
                    ToolInvocationPolicy.Ask,
                    DateTimeOffset.UtcNow);
                var decision = await callbacks.OnToolApprovalRequested!(pending, cancellationToken);
                Assert.Equal(ToolApprovalDecision.Approved, decision);
                if (callbacks.OnStreamEvent is { } onEvent)
                {
                    await onEvent(new AgentStreamEvent.TextMessageContent("m1", "ok"));
                }

                return session;
            }
        });

        using var http = harness.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Url + "v1/turns")
        {
            Content = JsonContent.Create(new CliTurnRequest { Cwd = harness.Cwd, Input = "write" }, options: JsonFileStoreOptions.WebCompactRelaxed)
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.IsSuccessStatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var readTask = ReadUntilAsync(stream, CliSseEventNames.ApprovalRequired, TimeSpan.FromSeconds(5));

        var approvalFrame = await readTask;
        using var doc = JsonDocument.Parse(approvalFrame.Data);
        var toolCallId = doc.RootElement.GetProperty("toolCallId").GetString();
        using var approvalResponse = await http.PostAsJsonAsync(
            harness.Url + "v1/approvals",
            new CliApprovalRequest { ToolCallId = toolCallId!, Decision = "approved" },
            JsonFileStoreOptions.WebCompactRelaxed);
        Assert.True(approvalResponse.IsSuccessStatusCode);

        var rest = await ReadAllAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Contains(rest, e => e.Event == CliSseEventNames.Done);
    }

    [Fact]
    public async Task Cancel_StopsInFlightTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await CliIpcHarness.StartAsync(orchestrator: new StubOrchestrator
        {
            Handler = async (session, _, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return session;
            }
        });

        using var http = harness.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Url + "v1/turns")
        {
            Content = JsonContent.Create(new CliTurnRequest { Cwd = harness.Cwd, Input = "slow" }, options: JsonFileStoreOptions.WebCompactRelaxed)
        };
        var responseTask = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var sessionId = harness.Storage.LastSaved?.Id;
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        using var cancelResponse = await http.PostAsync(harness.Url + $"v1/turns/{sessionId}/cancel", content: null);
        Assert.True(cancelResponse.IsSuccessStatusCode);

        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        await using var stream = await response.Content.ReadAsStreamAsync();
        var events = await ReadAllAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Contains(events, e => e.Event == CliSseEventNames.Error && e.Data.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Turn_WhenDesktopSessionRunning_ReturnsConflict()
    {
        await using var harness = await CliIpcHarness.StartAsync(probe: new AlwaysRunningProbe());
        using var http = harness.CreateClient();
        using var response = await http.PostAsJsonAsync(
            harness.Url + "v1/turns",
            new CliTurnRequest { Cwd = harness.Cwd, Input = "hi" },
            JsonFileStoreOptions.WebCompactRelaxed);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Turn_ResumesMappedSession()
    {
        await using var harness = await CliIpcHarness.StartAsync();
        await harness.PostTurnAsync(harness.Cwd, "first");
        var firstId = harness.Storage.LastSaved!.Id;
        await harness.PostTurnAsync(harness.Cwd, "second");
        Assert.Equal(firstId, harness.Storage.LastSaved!.Id);
        Assert.Equal(2, harness.Storage.SavedCount);
    }

    private static async Task<CliParsedSse> ReadUntilAsync(Stream stream, string eventName, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var frame in CliSseReader.ReadAsync(stream, cts.Token))
        {
            if (frame.Event == eventName)
            {
                return frame;
            }
        }

        throw new TimeoutException($"Did not receive SSE event {eventName}.");
    }

    private static async Task<List<CliParsedSse>> ReadAllAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var events = new List<CliParsedSse>();
        await foreach (var frame in CliSseReader.ReadAsync(stream, cts.Token))
        {
            events.Add(frame);
        }

        return events;
    }

    private sealed class AlwaysRunningProbe : IDesktopSessionRunProbe
    {
        public bool IsSessionRunning(string sessionId) => true;
    }

    private sealed class StubOrchestrator : IAgentOrchestrator
    {
        public Func<AgentSession, string, AgentTurnCallbacks?, CancellationToken, Task<AgentSession>>? Handler { get; init; }

        public Task<AgentSession> SendAsync(
            AgentSession session,
            string userInput,
            IReadOnlyList<ImageAttachment>? imageAttachments = null,
            AgentTurnCallbacks? callbacks = null,
            CancellationToken cancellationToken = default,
            bool computerUseActive = false,
            bool appendUserMessage = true)
        {
            if (Handler is not null)
            {
                return Handler(session, userInput, callbacks, cancellationToken);
            }

            return Task.FromResult(session.WithMessage(ChatMessage.Create(MessageRole.Assistant, "ok")));
        }
    }

    private sealed class MemoryStorage : IFileStorageService
    {
        private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
        public string RootPath => "/tmp";
        public AgentSession? LastSaved { get; private set; }
        public int SavedCount { get; private set; }

        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
        {
            _sessions[session.Id] = session;
            LastSaved = session;
            SavedCount++;
            return Task.CompletedTask;
        }

        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/t.jsonl");
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/e.txt");
        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        public Task ReplaceConversationDisplayAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
    }

    private sealed class CliIpcHarness : IAsyncDisposable
    {
        private readonly TempDirectoryScope _temp;
        private readonly CliIpcServer _server;

        private CliIpcHarness(TempDirectoryScope temp, CliIpcServer server, MemoryStorage storage, string cwd)
        {
            _temp = temp;
            _server = server;
            Storage = storage;
            Cwd = cwd;
        }

        public MemoryStorage Storage { get; }
        public string Cwd { get; }
        public string Url => _server.Url ?? throw new InvalidOperationException("server not started");
        public string Token => _server.Token ?? throw new InvalidOperationException("server not started");

        public static async Task<CliIpcHarness> StartAsync(
            IAgentOrchestrator? orchestrator = null,
            IDesktopSessionRunProbe? probe = null)
        {
            var temp = new TempDirectoryScope("cli-ipc");
            var cwd = Path.Combine(temp.Root, "workspace");
            Directory.CreateDirectory(cwd);
            var paths = new CliTestPathProvider(temp.Root);
            var storage = new MemoryStorage();
            var server = new CliIpcServer(
                orchestrator ?? new StubOrchestrator(),
                storage,
                new CliSessionMap(paths),
                paths,
                probe ?? NullDesktopSessionRunProbe.Instance,
                new NoOpLogger());
            await server.StartAsync();
            return new CliIpcHarness(temp, server, storage, cwd);
        }

        public HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return http;
        }

        public async Task<List<CliParsedSse>> PostTurnAsync(string cwd, string input)
        {
            using var http = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, Url + "v1/turns")
            {
                Content = JsonContent.Create(
                    new CliTurnRequest { Cwd = cwd, Input = input },
                    options: JsonFileStoreOptions.WebCompactRelaxed)
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            await using var stream = await response.Content.ReadAsStreamAsync();
            return await ReadAllAsync(stream, TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            _temp.Dispose();
        }
    }
}
