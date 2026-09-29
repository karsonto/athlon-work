using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.History;

namespace Athlon.Agent.Tests;

public sealed class ContextMemoryTests
{
    [Fact]
    public void Token_budget_notice_does_not_change_runtime_context_fingerprint()
    {
        var cache = new ModelMessageCache();
        var state = new RuntimeContextInjectionState();
        var history = new[] { ChatMessage.Create(MessageRole.User, "question") };
        var settings = new ContextCompactionSettings();

        var first = ModelMessagesForApiBuilder.Build(
            cache, "system", history, settings, "runtime one", state, "<token_budget>\nYou have 10 tokens left.\n</token_budget>");
        var second = ModelMessagesForApiBuilder.Build(
            cache, "system", history, settings, "runtime one", state, "<token_budget>\nYou have 9 tokens left.\n</token_budget>");

        Assert.False(state.FingerprintChanged);
        Assert.Equal("runtime one", second.Messages[^2].Content);
        Assert.Contains("9 tokens left", Assert.IsType<string>(second.Messages[^1].Content), StringComparison.Ordinal);
        Assert.DoesNotContain(second.Messages, message => message.Content is string text && text.Contains("Runtime context updated.", StringComparison.Ordinal));
        Assert.Contains("10 tokens left", Assert.IsType<string>(first.Messages[^1].Content), StringComparison.Ordinal);
    }

    [Fact]
    public void High_pressure_notice_names_the_handoff_and_compact_tools()
    {
        var budget = new ContextBudgetSnapshot(
            TotalWindow: 1000,
            ReservedOutput: 100,
            FixedOverhead: 800,
            HistoryBudget: 100,
            EstimatedHistory: 50,
            HistoryUtilization: 0.5);
        var notice = TokenBudgetNotice.Format(budget, ContextPressureLevel.High);

        Assert.Contains("session_note_append", notice, StringComparison.Ordinal);
        Assert.Contains("request_context_compact", notice, StringComparison.Ordinal);

        var normal = TokenBudgetNotice.Format(budget, ContextPressureLevel.Normal);
        Assert.DoesNotContain("session_note_append", normal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_tools_stay_inside_the_active_session_and_truncate()
    {
        using var temp = new TempDirectoryScope("athlon-history");
        var paths = new ArchivePathProvider(temp.Root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessions = new ActiveAgentSessionContext();
        var owner = AgentSession.Create("owner");
        var other = AgentSession.Create("other");
        await storage.SaveTranscriptAsync(
            owner.Id,
            [ChatMessage.Create(MessageRole.User, "SECRET-NEEDLE in the archive")],
            CancellationToken.None);
        var huge = new string('z', ContextTokenEstimator.EstimateCharacterBudget(SessionArchiveLimits.MaxToolOutputTokens) + 500);
        await storage.SaveEvictedToolResultAsync(owner.Id, "call-1", huge, CancellationToken.None);
        await storage.SaveEvictedToolResultAsync(other.Id, "call-1", "other-session-body", CancellationToken.None);

        sessions.SetSession(other.Id);
        var foreignSearch = await new HistorySearchTranscriptsTool(storage, sessions).InvokeAsync(
            new ToolInvocation("history_search_transcripts", new Dictionary<string, string> { ["query"] = "SECRET-NEEDLE" }));
        Assert.Contains("No archived transcript", foreignSearch.Content, StringComparison.Ordinal);

        var traversal = await new HistoryReadTranscriptTool(storage, sessions).InvokeAsync(
            new ToolInvocation("history_read_transcript", new Dictionary<string, string> { ["file_name"] = "../owner/transcript_1.jsonl" }));
        Assert.False(traversal.Succeeded);

        var foreignEvicted = await new HistoryReadEvictedTool(storage, sessions).InvokeAsync(
            new ToolInvocation("history_read_evicted", new Dictionary<string, string> { ["tool_call_id"] = "call-1" }));
        Assert.Equal("other-session-body", foreignEvicted.Content);
        Assert.DoesNotContain("zzzz", foreignEvicted.Content ?? string.Empty, StringComparison.Ordinal);

        sessions.SetSession(owner.Id);
        var search = await new HistorySearchTranscriptsTool(storage, sessions).InvokeAsync(
            new ToolInvocation("history_search_transcripts", new Dictionary<string, string> { ["query"] = "secret-needle" }));
        Assert.Contains("No archived transcript", search.Content, StringComparison.Ordinal);

        var found = await new HistorySearchTranscriptsTool(storage, sessions).InvokeAsync(
            new ToolInvocation("history_search_transcripts", new Dictionary<string, string> { ["query"] = "SECRET-NEEDLE" }));
        Assert.Contains("SECRET-NEEDLE", found.Content, StringComparison.Ordinal);

        var evicted = await new HistoryReadEvictedTool(storage, sessions).InvokeAsync(
            new ToolInvocation("history_read_evicted", new Dictionary<string, string> { ["tool_call_id"] = "call-1" }));
        Assert.Contains("...(truncated)", evicted.Content, StringComparison.Ordinal);
        Assert.True(evicted.Content!.Length < huge.Length);

        var escaped = await new HistoryReadEvictedTool(storage, sessions).InvokeAsync(
            new ToolInvocation("history_read_evicted", new Dictionary<string, string> { ["tool_call_id"] = "../other/call-1" }));
        Assert.False(escaped.Succeeded);
    }

    [Fact]
    public async Task Compaction_reattaches_handoff_note_after_the_summary()
    {
        using var temp = new TempDirectoryScope("athlon-handoff");
        var paths = new ArchivePathProvider(temp.Root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("handoff-session").WithMessages(
        [
            ChatMessage.Create(MessageRole.User, "old"),
            ChatMessage.Create(MessageRole.Assistant, "earlier"),
            ChatMessage.Create(MessageRole.User, "current question")
        ]);
        Assert.True(await storage.TryAppendHandoffNoteAsync(session.Id, "Goal: finish the migration.\nNext: run tests."));

        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                TriggerMessages = 2,
                KeepMessages = 1,
                SummaryMaxTokens = 256,
                MaxConversationCharsForSummary = 10_000
            }
        };
        var result = await new ConversationCompactor(
            settings,
            new SummaryModelClient(),
            storage,
            new TruncateArgsService(),
            new SessionUsageAccumulator(),
            new NoOpLogger()).CompactIfNeededAsync(
            session,
            new CompactionExecutionRequest(CompactionKind.ConversationCompact, Force: false, EmitAudit: true));

        Assert.True(result.Compacted);
        var summaryIndex = result.Session.Messages.ToList().FindIndex(SummaryMessageBuilder.IsSummaryMessage);
        Assert.True(summaryIndex >= 0);
        Assert.True(SessionHandoffNote.IsHandoffMessage(result.Session.Messages[summaryIndex + 1]));
        Assert.Contains("finish the migration", result.Session.Messages[summaryIndex + 1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_context_compact_is_consumed_once_on_the_next_round()
    {
        var store = new ContextCompactRequestStore();
        var pipeline = new RecordingPipeline();
        var middleware = new CompactionTurnMiddleware(
            pipeline,
            new NoOpTokenEstimator(),
            new PromptPressureStore(),
            new NoOpStorage(),
            new AppSettings(),
            transcriptWriter: null,
            compactRequests: store);
        var session = AgentSession.Create("compact-request");
        var sessions = new ActiveAgentSessionContext();
        sessions.SetSession(session.Id);

        var requested = await new RequestContextCompactTool(store, sessions).InvokeAsync(
            new ToolInvocation("request_context_compact", new Dictionary<string, string>()));
        Assert.True(requested.Succeeded);

        await middleware.OnBeforeModelRoundAsync(CreateInvocation(session), CancellationToken.None);
        Assert.True(pipeline.Options.ForceConversationCompact);
        Assert.True(pipeline.Options.ApplyEvenWhenBelowThreshold);

        await middleware.OnBeforeModelRoundAsync(CreateInvocation(session), CancellationToken.None);
        Assert.False(pipeline.Options.ForceConversationCompact);
    }

    private static AgentTurnInvocation CreateInvocation(AgentSession session) =>
        new()
        {
            RunContext = AgentRunContext.CreateRoot(
                session,
                "run-1",
                new ToolRouter(Array.Empty<IAgentTool>()),
                PromptTestHelpers.CreateStaticOrchestrator(),
                []),
            Session = session,
            StreamAdapter = new AgentStreamAdapter(session.Id, "run-1"),
            EnvironmentPrompt = "system",
            Tools = []
        };

    private sealed class RecordingPipeline : IPreCompletionPipeline
    {
        public PreCompletionOptions Options { get; private set; } = PreCompletionOptions.Default;

        public Task<AgentSession> RunAsync(
            AgentSession session,
            PreCompletionOptions? options = null,
            CompactionRuntimeContext? runtimeContext = null,
            CancellationToken cancellationToken = default)
        {
            Options = options ?? PreCompletionOptions.Default;
            return Task.FromResult(session);
        }
    }

    private sealed class SummaryModelClient : IAgentModelClient
    {
        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentModelResponse("summary text", []));
    }

    private sealed class ArchivePathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(SessionsPath);
        }

        public string ResolveSkillPath(string path) => path;
    }
}
