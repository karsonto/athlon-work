using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Memory;

namespace Athlon.Agent.Tests;

public sealed class ParallelToolExecutionTests
{
    [Theory]
    [InlineData("file_read", true)]
    [InlineData("grep_files", true)]
    [InlineData("glob_files", true)]
    [InlineData("file_list", true)]
    [InlineData("memory_search", true)]
    [InlineData("memory_get", false)]
    [InlineData("file_write", false)]
    [InlineData("execute_command", false)]
    public void IsParallelizable_MatchesAllowlist(string toolName, bool expected) =>
        Assert.Equal(expected, ParallelToolPolicy.IsParallelizable(toolName));

    [Fact]
    public void CanParallelizeBatch_RequiresAllParallelizableAndCountGreaterThanOne()
    {
        var settings = new ParallelToolExecutionSettings { Enabled = true };
        var parallelBatch = new[]
        {
            new AgentToolCall("1", "file_read", new Dictionary<string, string> { ["path"] = "a" }),
            new AgentToolCall("2", "grep_files", new Dictionary<string, string> { ["pattern"] = "x" })
        };
        var mixedBatch = new[]
        {
            new AgentToolCall("1", "file_read", new Dictionary<string, string> { ["path"] = "a" }),
            new AgentToolCall("2", "file_write", new Dictionary<string, string> { ["path"] = "a", ["content"] = "b" })
        };

        Assert.True(ParallelToolPolicy.CanParallelizeBatch(parallelBatch, settings));
        Assert.False(ParallelToolPolicy.CanParallelizeBatch(mixedBatch, settings));
        Assert.False(ParallelToolPolicy.CanParallelizeBatch(
            [new AgentToolCall("1", "file_read", new Dictionary<string, string>())],
            settings));
    }

    [Fact]
    public void CanParallelizeBatch_WhenDisabled_ReturnsFalse()
    {
        var calls = new[]
        {
            new AgentToolCall("1", "file_read", new Dictionary<string, string>()),
            new AgentToolCall("2", "file_list", new Dictionary<string, string>())
        };

        Assert.False(ParallelToolPolicy.CanParallelizeBatch(
            calls,
            new ParallelToolExecutionSettings { Enabled = false }));
    }

    [Fact]
    public void MarkedTools_AreListedInPolicy()
    {
        Assert.Contains(FileReadToolName(), ParallelToolPolicy.AllowedToolNames);
        Assert.Contains(GrepFilesToolName(), ParallelToolPolicy.AllowedToolNames);
        Assert.Contains(GlobFilesToolName(), ParallelToolPolicy.AllowedToolNames);
        Assert.Contains(FileListToolName(), ParallelToolPolicy.AllowedToolNames);
        Assert.Contains(MemorySearchToolName(), ParallelToolPolicy.AllowedToolNames);
    }

    [Fact]
    public async Task SendAsync_ParallelReadOnlyBatch_RunsConcurrently()
    {
        var storage = new NoOpStorage();
        var router = new ConcurrentTrackingToolRouter(TimeSpan.FromMilliseconds(80));
        var settings = new AppSettings
        {
            ParallelToolExecution = new ParallelToolExecutionSettings
            {
                Enabled = true,
                MaxDegreeOfParallelism = 4
            }
        };
        var runtime = CreateRuntime(storage, router, settings, new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("c1", "file_read", new Dictionary<string, string> { ["path"] = "a.txt" }),
                new AgentToolCall("c2", "file_read", new Dictionary<string, string> { ["path"] = "b.txt" }),
                new AgentToolCall("c3", "grep_files", new Dictionary<string, string> { ["pattern"] = "class" })
            }),
            new AgentModelResponse("done", Array.Empty<AgentToolCall>())));

        await runtime.SendAsync(AgentSession.Create("parallel-read"), "search");

        Assert.Equal(3, router.InvokeCount);
        Assert.True(router.MaxConcurrent >= 2, $"expected concurrent execution, max={router.MaxConcurrent}");
    }

    [Fact]
    public async Task SendAsync_ParallelReadOnlyBatch_PreservesToolMessageOrder()
    {
        var storage = new NoOpStorage();
        var router = new ConcurrentTrackingToolRouter(TimeSpan.FromMilliseconds(30));
        var settings = new AppSettings { ParallelToolExecution = new ParallelToolExecutionSettings { Enabled = true } };
        var runtime = CreateRuntime(storage, router, settings, new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("first", "file_read", new Dictionary<string, string> { ["path"] = "1" }),
                new AgentToolCall("second", "file_read", new Dictionary<string, string> { ["path"] = "2" })
            }),
            new AgentModelResponse("done", Array.Empty<AgentToolCall>())));

        var session = await runtime.SendAsync(AgentSession.Create("order-test"), "go");
        var toolMessages = session.Messages.Where(message => message.Role == MessageRole.Tool).ToArray();

        Assert.Equal(2, toolMessages.Length);
        Assert.Contains("first", toolMessages[0].Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("second", toolMessages[1].Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_MixedBatch_FallsBackToSerial()
    {
        var storage = new NoOpStorage();
        var router = new ConcurrentTrackingToolRouter(TimeSpan.FromMilliseconds(80));
        var settings = new AppSettings { ParallelToolExecution = new ParallelToolExecutionSettings { Enabled = true } };
        var runtime = CreateRuntime(storage, router, settings, new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("c1", "file_read", new Dictionary<string, string> { ["path"] = "a.txt" }),
                new AgentToolCall("c2", "file_write", new Dictionary<string, string> { ["path"] = "a.txt", ["content"] = "x" })
            }),
            new AgentModelResponse("done", Array.Empty<AgentToolCall>())));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runtime.SendAsync(AgentSession.Create("serial-mixed"), "write");
        sw.Stop();

        Assert.Equal(2, router.InvokeCount);
        Assert.Equal(1, router.MaxConcurrent);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150), $"expected serial wall time, actual={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task SendAsync_ParallelBatch_SuppressesDuplicateGrepViaToolStorm()
    {
        var storage = new NoOpStorage();
        var router = new ConcurrentTrackingToolRouter(TimeSpan.FromMilliseconds(5));
        var settings = new AppSettings
        {
            ParallelToolExecution = new ParallelToolExecutionSettings { Enabled = true },
            ContextCompaction = new ContextCompactionSettings
            {
                ToolStorm = new ToolStormSettings
                {
                    Enabled = true,
                    Scope = ToolStormScope.Turn,
                    Threshold = 3,
                    WindowSize = 8
                }
            }
        };
        var args = new Dictionary<string, string> { ["pattern"] = "same" };
        var runtime = CreateRuntime(storage, router, settings, new ScriptedModelClient(
            new AgentModelResponse(string.Empty, new[]
            {
                new AgentToolCall("g1", "grep_files", args),
                new AgentToolCall("g2", "grep_files", args),
                new AgentToolCall("g3", "grep_files", args)
            }),
            new AgentModelResponse("done", Array.Empty<AgentToolCall>())));

        var session = await runtime.SendAsync(AgentSession.Create("tool-storm-parallel"), "grep");
        var toolMessages = session.Messages.Where(message => message.Role == MessageRole.Tool).ToArray();

        Assert.Equal(3, toolMessages.Length);
        Assert.Equal(2, router.InvokeCount);
        Assert.Contains("suppressed", toolMessages[2].Content ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAsync_AskToolWithoutApprovalCallback_RemainsPending()
    {
        var router = new ApprovalTrackingToolRouter();
        var runtime = CreateRuntime(
            new NoOpStorage(),
            router,
            new AppSettings(),
            new ScriptedModelClient(
                new AgentModelResponse(
                    string.Empty,
                    [new AgentToolCall("ask-1", "file_write", new Dictionary<string, string> { ["path"] = "a.txt" })]),
                new AgentModelResponse("done", Array.Empty<AgentToolCall>())));

        var session = await runtime.SendAsync(AgentSession.Create("approval-pending"), "write");

        Assert.Equal(0, router.InvokeCount);
        Assert.Contains(
            "policy.approval_required",
            session.Messages.Single(message => message.Role == MessageRole.Tool).Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_InvalidArguments_AreRejectedBeforeApprovalOrExecution()
    {
        var router = new ApprovalTrackingToolRouter();
        var storage = new NoOpStorage();
        var runtime = CreateRuntime(
            storage,
            router,
            new AppSettings(),
            new ScriptedModelClient(
                new AgentModelResponse(
                    string.Empty,
                    [new AgentToolCall("invalid-1", "file_write", ToolCallArgumentsParser.ParseJson("""{"path":42}"""))]),
                new AgentModelResponse("done", Array.Empty<AgentToolCall>())));
        var approvalRequests = 0;
        var callbacks = new AgentTurnCallbacks
        {
            OnToolApprovalRequested = (_, _) =>
            {
                approvalRequests++;
                return Task.FromResult(ToolApprovalDecision.Approved);
            }
        };

        var session = await runtime.SendAsync(
            AgentSession.Create("invalid-before-approval"),
            "write",
            callbacks: callbacks);

        Assert.Equal(0, approvalRequests);
        Assert.Equal(0, router.InvokeCount);
        Assert.Contains(
            "schema.type_mismatch",
            session.Messages.Single(message => message.Role == MessageRole.Tool).Content,
            StringComparison.Ordinal);
        var rejection = Assert.Single(storage.Attempts, item => item.Kind == AgentAttemptKind.Tool);
        Assert.Equal("schema.type_mismatch", rejection.ErrorCode);
        Assert.Equal("failure", rejection.Result);
        Assert.NotNull(rejection.SchemaFingerprint);
    }

    [Fact]
    public async Task SendAsync_AskToolExecutesAfterApprovalCallbackApproves()
    {
        var router = new ApprovalTrackingToolRouter();
        var runtime = CreateRuntime(
            new NoOpStorage(),
            router,
            new AppSettings(),
            new ScriptedModelClient(
                new AgentModelResponse(
                    string.Empty,
                    [new AgentToolCall("ask-1", "file_write", new Dictionary<string, string> { ["path"] = "a.txt" })]),
                new AgentModelResponse("done", Array.Empty<AgentToolCall>())));
        PendingToolApproval? requested = null;
        var callbacks = new AgentTurnCallbacks
        {
            OnToolApprovalRequested = (pending, _) =>
            {
                requested = pending;
                return Task.FromResult(ToolApprovalDecision.Approved);
            }
        };

        await runtime.SendAsync(AgentSession.Create("approval-approved"), "write", callbacks: callbacks);

        Assert.Equal(1, router.InvokeCount);
        Assert.Equal("ask-1", requested?.ToolCallId);
    }

    [Fact]
    public void SubAgentSettings_DefaultMaxConcurrentSubAgents_IsTen() =>
        Assert.Equal(10, new SubAgentSettings().MaxConcurrentSubAgents);

    private static AgentRuntime CreateRuntime(
        IFileStorageService storage,
        IToolRouter toolRouter,
        AppSettings settings,
        IAgentModelClient modelClient)
    {
        var logger = new NoOpLogger();
        var (pipeline, compaction) = AgentRuntimeTestFactory.CreateMiddleware(
            new NoOpPreCompletionPipeline(),
            storage,
            new TokenEstimatorCalibrator(settings),
            settings,
            logger);
        return new AgentRuntime(
            modelClient,
            storage,
            toolRouter,
            PromptTestHelpers.CreateStaticOrchestrator("test prompt"),
            new NoOpPreCompletionPipeline(),
            new PassThroughToolResultEvictor(),
            new TokenEstimatorCalibrator(settings),
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            new NoOpActiveAgentSessionContext(),
            new AgentRunContextAccessor(),
            pipeline,
            compaction,
            settings,
            logger,
            new NoOpPostTurnMemoryProcessor());
    }

    private static string FileReadToolName() =>
        new FileReadTool(RouterTestDependencies.CreateWorkspaceGuard(), CreateAudit(), new AppSettings()).Definition.Name;

    private static string GrepFilesToolName() =>
        new GrepFilesTool(RouterTestDependencies.CreateWorkspaceGuard(), CreateAudit(), new AppSettings()).Definition.Name;

    private static string GlobFilesToolName() =>
        new GlobFilesTool(RouterTestDependencies.CreateWorkspaceGuard(), CreateAudit()).Definition.Name;

    private static string FileListToolName() =>
        new FileListTool(RouterTestDependencies.CreateWorkspaceGuard(), CreateAudit()).Definition.Name;

    private static string MemorySearchToolName() =>
        new MemorySearchTool(new StubLongTermMemory(), new NoOpLogger()).Definition.Name;

    private static AuditLogService CreateAudit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new AuditLogService(new NoOpLogger(), new TestPathProvider(root), new JsonFileStore());
    }

    private sealed class TestPathProvider(string rootPath) : IAppPathProvider
    {
        public string RootPath { get; } = rootPath;
        public string ConfigPath => Path.Combine(rootPath, "config");
        public string SessionsPath => Path.Combine(rootPath, "sessions");
        public string AuditPath => Path.Combine(rootPath, "audit");
        public string LogsPath => Path.Combine(rootPath, "logs");
        public string CredentialsPath => Path.Combine(rootPath, "credentials");
        public string SkillsPath => Path.Combine(rootPath, "skills");
        public void EnsureCreated() => Directory.CreateDirectory(rootPath);
        public string ResolveSkillPath(string path) => Path.Combine(SkillsPath, path);
    }

    private sealed class ConcurrentTrackingToolRouter(TimeSpan delay) : IToolRouter
    {
        private int _active;
        private int _maxConcurrent;
        private int _invokeCount;

        public int MaxConcurrent => _maxConcurrent;
        public int InvokeCount => _invokeCount;

        public IReadOnlyList<ToolDefinition> ListTools() =>
        [
            new ToolDefinition("file_read", "read", ToolSchema.Object().AllowAdditionalProperties().Build()),
            new ToolDefinition("grep_files", "grep", ToolSchema.Object().AllowAdditionalProperties().Build()),
            new ToolDefinition("file_write", "write", ToolSchema.Object().AllowAdditionalProperties().Build())
        ];

        public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invokeCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMax(active);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                return ToolResult.Success($"ran {invocation.ToolName}", $"ToolCallId: {invocation.ToolName}");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMax(int active)
        {
            while (true)
            {
                var current = _maxConcurrent;
                if (active <= current || Interlocked.CompareExchange(ref _maxConcurrent, active, current) == current)
                {
                    break;
                }
            }
        }
    }

    private sealed class ApprovalTrackingToolRouter : IToolRouter
    {
        public int InvokeCount { get; private set; }

        public IReadOnlyList<ToolDefinition> ListTools() =>
        [
            new ToolDefinition(
                "file_write",
                "approval test",
                ToolSchema.Object().String("path", "path", required: true).Build(),
                InvocationPolicy: ToolInvocationPolicy.Ask)
        ];

        public Task<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            InvokeCount++;
            return Task.FromResult(ToolResult.Success("executed"));
        }
    }

    private sealed class ScriptedModelClient(params AgentModelResponse[] responses) : IAgentModelClient
    {
        private int _index;

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            if (_index >= responses.Length)
            {
                throw new InvalidOperationException("No more scripted responses.");
            }

            return Task.FromResult(responses[_index++]);
        }
    }

    private sealed class StubLongTermMemory : ILongTermMemory
    {
        public Task<string> ReadCuratedAsync(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task AppendDailyAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> ReadDailyAsync(DateTime date, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(DateTime watermark, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string> ReadDailyFileAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task WriteCuratedAsync(string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTime> ReadWatermarkAsync(CancellationToken cancellationToken = default) => Task.FromResult(DateTime.MinValue);
        public Task WriteWatermarkAsync(DateTime watermark, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ArchiveDailyFileAsync(string relativePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class NoOpPreCompletionPipeline : IPreCompletionPipeline
    {
        public Task<AgentSession> RunAsync(
            AgentSession session,
            PreCompletionOptions? options = null,
            CompactionRuntimeContext? runtimeContext = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(session);
    }

    private sealed class PassThroughToolResultEvictor : IToolResultEvictor
    {
        public Task<string> EvictIfNeededAsync(
            string sessionId,
            AgentToolCall toolCall,
            ToolResult result,
            string formattedToolContent,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(formattedToolContent);
    }

    private sealed class NoOpStorage : IFileStorageService
    {
        public List<AgentAttemptEvent> Attempts { get; } = [];
        public string RootPath => "/tmp";
        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<AgentSession?>(null);
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        public Task ReplaceConversationDisplayAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendAttemptEventAsync(string sessionId, AgentAttemptEvent entry, CancellationToken cancellationToken = default)
        {
            Attempts.Add(entry);
            return Task.CompletedTask;
        }
        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
