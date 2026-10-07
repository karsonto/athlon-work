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
        var appended = await storage.TryAppendHandoffNoteAsync(session.Id, "Goal: finish the migration.\nNext: run tests.");
        Assert.True(appended.Written);

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
        var model = new SummaryModelClient();
        var result = await new ConversationCompactor(
            settings,
            model,
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
        Assert.Contains("[session-handoff]", result.Session.Messages[summaryIndex].Content, StringComparison.Ordinal);
        var summaryPrompt = string.Join(
            "\n",
            model.LastRequest!.Messages.Select(message => message.Content as string ?? string.Empty));
        Assert.Contains(ConversationCompactionDefaults.HandoffPreservedSummaryOverride, summaryPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("finish the migration", summaryPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handoff_append_over_the_limit_keeps_the_existing_note()
    {
        using var temp = new TempDirectoryScope("athlon-handoff-limit");
        var paths = new ArchivePathProvider(temp.Root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("limit-session");
        var filler = new string('a', SessionHandoffNote.MaxChars - 10);
        var written = await storage.TryAppendHandoffNoteAsync(session.Id, filler);
        Assert.True(written.Written);
        Assert.Equal(filler.Length, written.CurrentChars);
        Assert.Equal(10, written.RemainingChars);

        var rejected = await storage.TryAppendHandoffNoteAsync(session.Id, new string('b', 20));
        Assert.False(rejected.Written);
        Assert.Equal(filler.Length, rejected.CurrentChars);
        Assert.Equal(SessionHandoffNote.MaxChars, rejected.MaxChars);
        Assert.Equal(10, rejected.RemainingChars);
        Assert.Equal(filler, await storage.ReadHandoffNoteAsync(session.Id));

        var sessions = new ActiveAgentSessionContext();
        sessions.SetSession(session.Id);
        var toolResult = await new SessionNoteAppendTool(storage, sessions).InvokeAsync(
            new ToolInvocation("session_note_append", new Dictionary<string, string> { ["text"] = new string('c', 20) }));
        Assert.False(toolResult.Succeeded);
        Assert.Contains("existing note is unchanged", toolResult.Error, StringComparison.Ordinal);
        Assert.Contains("current: " + filler.Length, toolResult.Error, StringComparison.Ordinal);
        Assert.Contains("remaining: 10", toolResult.Error, StringComparison.Ordinal);
        Assert.Equal(filler, await storage.ReadHandoffNoteAsync(session.Id));
    }

    [Fact]
    public async Task Compaction_drops_a_stale_handoff_message_and_keeps_the_disk_note()
    {
        using var temp = new TempDirectoryScope("athlon-handoff-dedupe");
        var paths = new ArchivePathProvider(temp.Root);
        paths.EnsureCreated();
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var session = AgentSession.Create("dedupe-session").WithMessages(
        [
            ChatMessage.Create(MessageRole.User, "old"),
            ChatMessage.Create(MessageRole.Assistant, "earlier"),
            SessionHandoffNote.CreateMessage("OLD-NOTE should disappear"),
            ChatMessage.Create(MessageRole.User, "current question")
        ]);
        Assert.True((await storage.TryAppendHandoffNoteAsync(session.Id, "DISK-NOTE is authoritative")).Written);

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
        var model = new SummaryModelClient();
        var result = await new ConversationCompactor(
            settings,
            model,
            storage,
            new TruncateArgsService(),
            new SessionUsageAccumulator(),
            new NoOpLogger()).CompactIfNeededAsync(
            session,
            new CompactionExecutionRequest(CompactionKind.ConversationCompact, Force: false, EmitAudit: true));

        Assert.True(result.Compacted);
        var handoffs = result.Session.Messages.Where(SessionHandoffNote.IsHandoffMessage).ToList();
        var kept = Assert.Single(handoffs);
        Assert.Contains("DISK-NOTE", kept.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD-NOTE", kept.Content, StringComparison.Ordinal);
        var summaryIndex = result.Session.Messages.ToList().FindIndex(SummaryMessageBuilder.IsSummaryMessage);
        Assert.Equal(summaryIndex + 1, result.Session.Messages.ToList().IndexOf(kept));
        var summaryPrompt = string.Join(
            "\n",
            model.LastRequest!.Messages.Select(message => message.Content as string ?? string.Empty));
        Assert.DoesNotContain("OLD-NOTE", summaryPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("DISK-NOTE", summaryPrompt, StringComparison.Ordinal);
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
        public AgentModelRequest? LastRequest { get; private set; }

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AgentModelResponse("summary text", []));
        }
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
