using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

/// <summary>
/// Covers the cut-point rework (protected-tail cap + dual anchor) and the measured-token budget
/// resolution that keeps the composer occupancy meter honest.
/// </summary>
public sealed class CompactionStrategyReworkTests
{
    private const int ProtectedTailMessages = 20;

    // ---------- cut plan ----------

    [Fact]
    public void DetermineCutPlan_ShortConversation_LeavesAnchorBehaviourUnchanged()
    {
        var settings = CreateSemanticSettings();
        var conversation = new[]
        {
            ChatMessage.Create(MessageRole.User, "first"),
            ChatMessage.Create(MessageRole.Assistant, "a"),
            ChatMessage.Create(MessageRole.User, "latest"),
            ChatMessage.Create(MessageRole.Assistant, "b")
        };

        var plan = SemanticCutoffPlanner.DetermineCutPlan(conversation, settings, keepTokenBudget: 1);

        // The latest real user message still bounds the cut, and nothing needs re-attaching.
        Assert.Equal(2, plan.SummarizedEnd);
        Assert.Null(plan.UserAnchorIndex);
        Assert.False(plan.NeedsUserReattach);
    }

    [Fact]
    public void DetermineCutPlan_LongTurn_AdvancesPastUserAnchorAndReattachesIt()
    {
        var settings = CreateSemanticSettings();
        var conversation = BuildLongTurn(pairs: 30);
        var keepBudget = ContextTokenEstimator.EstimateSuffix(conversation, conversation.Count - 4);

        var plan = SemanticCutoffPlanner.DetermineCutPlan(conversation, settings, keepBudget);

        // Without the cap the cut would be pinned at the turn-opening user message (index 0) and the
        // whole turn would stay unsummarizable — the root cause of "compacted repeatedly but the
        // token count never falls". The cap lands on a pairing-balanced boundary, so the planner
        // advances 41 messages into the turn instead of 0.
        Assert.Equal(conversation.Count - ProtectedTailMessages, plan.SummarizedEnd);
        Assert.Equal(41, plan.SummarizedEnd);
        Assert.Equal(0, plan.UserAnchorIndex);
        Assert.True(plan.NeedsUserReattach);

        // Both boundaries must be pairing-safe: no orphaned tool_result in the retained tail.
        Assert.True(ConversationCutoffPlanner.IsPairingBalancedBefore(conversation, plan.SummarizedEnd));
        Assert.True(ConversationCutoffPlanner.IsPairingBalancedInRange(
            conversation,
            plan.RetainedTailStart,
            conversation.Count));
    }

    [Fact]
    public void DetermineCutPlan_ReattachDisabled_ReportsNoAnchor()
    {
        var settings = CreateSemanticSettings();
        settings.ReattachLatestUserMessage = false;
        var conversation = BuildLongTurn(pairs: 30);
        var keepBudget = ContextTokenEstimator.EstimateSuffix(conversation, conversation.Count - 4);

        var plan = SemanticCutoffPlanner.DetermineCutPlan(conversation, settings, keepBudget);

        Assert.Equal(41, plan.SummarizedEnd);
        Assert.Null(plan.UserAnchorIndex);
    }

    [Fact]
    public void DetermineCutPlan_HiddenControlMessagesNeverAnchorOrGetReattached()
    {
        var settings = CreateSemanticSettings();
        var conversation = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "real request"),
            ChatMessage.Create(MessageRole.Assistant, "ok"),
            ChatMessage.Create(MessageRole.User, PlanContinuePrompt.BuildUserMessage()),
            ChatMessage.Create(MessageRole.Assistant, "still going")
        };

        var plan = SemanticCutoffPlanner.DetermineCutPlan(conversation, settings, keepTokenBudget: 1);

        // If the hidden control message anchored the tail, the cut would land at index 2.
        Assert.Equal(0, plan.SummarizedEnd);
        Assert.Null(plan.UserAnchorIndex);
    }

    [Fact]
    public void DetermineCutPlan_LongTurnWithControlMessage_ReattachesTheRealUserMessage()
    {
        var settings = CreateSemanticSettings();
        var conversation = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "real request"),
            ChatMessage.Create(MessageRole.User, PlanContinuePrompt.BuildUserMessage())
        };
        AppendToolPairs(conversation, pairs: 30, startingIndex: 0);
        var keepBudget = ContextTokenEstimator.EstimateSuffix(conversation, conversation.Count - 4);

        var plan = SemanticCutoffPlanner.DetermineCutPlan(conversation, settings, keepBudget);

        var anchor = conversation[plan.UserAnchorIndex!.Value];
        Assert.Contains("real request", anchor.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanContinuePrompt.Marker, anchor.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void DetermineCutPlan_CapLandsOnToolResult_AdvancesToSafeBoundary()
    {
        var settings = CreateSemanticSettings();
        // 12 pairs is 25 messages, so a cap of 3 would naively cut at index 22 — the tool result of
        // pair 10. Retaining from there would leave that result without its tool_call.
        settings.ProtectedTailMaxMessages = 3;
        var conversation = BuildLongTurn(pairs: 12);
        var keepBudget = ContextTokenEstimator.EstimateSuffix(conversation, conversation.Count - 2);

        var naive = conversation.Count - settings.ProtectedTailMaxMessages;
        Assert.Equal(MessageRole.Tool, conversation[naive].Role);

        var plan = SemanticCutoffPlanner.DetermineCutPlan(conversation, settings, keepBudget);

        Assert.NotEqual(MessageRole.Tool, conversation[plan.SummarizedEnd].Role);
        Assert.True(ConversationCutoffPlanner.IsRetainedTailStartSafe(conversation, plan.SummarizedEnd));
        Assert.True(ConversationCutoffPlanner.IsPairingBalancedInRange(
            conversation,
            plan.RetainedTailStart,
            conversation.Count));
    }

    [Fact]
    public void CreatePlanForCutoff_CutOnAnUnpairedToolResult_AdvancesToTheNextBoundary()
    {
        // Legacy/malformed history can carry a tool result whose tool_call is nowhere in the prefix,
        // so the prefix balance check accepts a cutoff that would make the retained tail open on an
        // orphaned result. The retained-tail guard has to move it forward.
        var conversation = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "go"),
            ChatMessage.Create(MessageRole.Tool, "ToolCallId: ghost\nstale result"),
            ChatMessage.Create(MessageRole.Assistant, "next"),
            ChatMessage.Create(MessageRole.User, "more"),
            ChatMessage.Create(MessageRole.Assistant, "done")
        };

        Assert.True(ConversationCutoffPlanner.IsPairingBalancedBefore(conversation, 1));
        Assert.Equal(MessageRole.Tool, conversation[1].Role);

        var plan = SemanticCutoffPlanner.CreatePlanForCutoff(conversation, cutoff: 1);

        Assert.Equal(2, plan.SummarizedEnd);
        Assert.Equal(MessageRole.Assistant, conversation[plan.SummarizedEnd].Role);
        Assert.True(ConversationCutoffPlanner.IsRetainedTailStartSafe(conversation, plan.SummarizedEnd));
        Assert.True(ConversationCutoffPlanner.IsPairingBalancedInRange(
            conversation,
            plan.RetainedTailStart,
            conversation.Count));
    }

    // ---------- compactor ----------

    [Fact]
    public async Task CompactIfNeededAsync_LongTurn_ShrinksPayloadAndReattachesInstruction()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = CreateCompactionSettings();
            var paths = new CompactionTests.TestAppPathProvider(root);
            paths.EnsureCreated();
            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var capturing = new CapturingSummaryClient("condensed turn summary");
            var conversation = BuildLongTurn(pairs: 30);
            var session = AgentSession.Create("long-turn").WithMessages(conversation);
            var tokensBefore = ContextTokenEstimator.Estimate(conversation, includeReasoningInModelContext: false);

            var result = await new ConversationCompactor(
                settings,
                capturing,
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger()).CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: false,
                    EmitAudit: true));

            Assert.True(result.Compacted);
            var compacted = ConversationMessageFilters.WithoutCompactionAudits(result.Session.Messages);
            var tokensAfter = ContextTokenEstimator.Estimate(compacted, includeReasoningInModelContext: false);

            Assert.True(
                tokensAfter < tokensBefore,
                $"expected tokens to fall (before={tokensBefore}, after={tokensAfter})");
            Assert.True(tokensBefore - tokensAfter >= settings.ContextCompaction.MinCompactionSavingsTokens);

            // The turn-opening instruction survives verbatim, right after the summary placeholder.
            var summaryIndex = compacted.FindIndex(SummaryMessageBuilder.IsSummaryMessage);
            Assert.True(summaryIndex >= 0);
            Assert.Contains("do the task", compacted[summaryIndex + 1].Content, StringComparison.Ordinal);

            // And the retained tail has no orphaned tool results.
            Assert.True(ConversationCutoffPlanner.IsPairingBalancedBefore(compacted, compacted.Count));

            // The summary actually covered tool output, not just a placeholder.
            Assert.Contains("tool-output-", capturing.LastPrompt, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task CompactIfNeededAsync_RepeatedPasses_ConvergeInsteadOfResummarizing()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = CreateCompactionSettings();
            var paths = new CompactionTests.TestAppPathProvider(root);
            paths.EnsureCreated();
            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());

            async Task<(ConversationCompactResult Result, CountingSummaryClient Client)> RunAsync(AgentSession session)
            {
                var client = new CountingSummaryClient("summary");
                var result = await new ConversationCompactor(
                    settings,
                    client,
                    storage,
                    new TruncateArgsService(),
                    new SessionUsageAccumulator(),
                    new NoOpLogger()).CompactIfNeededAsync(
                    session,
                    new CompactionExecutionRequest(CompactionKind.ConversationCompact, Force: false, EmitAudit: true));
                return (result, client);
            }

            var session = AgentSession.Create("converge").WithMessages(BuildLongTurn(pairs: 30));
            var tokensBefore = TokensOf(session);
            var current = session;
            var compactPasses = 0;
            var converged = false;

            // Before the guard existed this loop never ended: every round re-summarized an already
            // summarized prefix while the token count stayed flat.
            for (var attempt = 0; attempt < 5 && !converged; attempt++)
            {
                var (result, client) = await RunAsync(current);
                if (!result.Compacted)
                {
                    Assert.Equal(0, client.CallCount);
                    converged = true;
                    break;
                }

                Assert.Equal(1, client.CallCount);
                var after = TokensOf(result.Session);
                Assert.True(after < TokensOf(current), $"pass {attempt + 1} did not shrink the payload");
                current = result.Session;
                compactPasses++;
            }

            Assert.True(converged, "compaction never settled — it keeps paying for summaries without converging");
            Assert.InRange(compactPasses, 1, 3);
            Assert.True(TokensOf(current) <= tokensBefore - settings.ContextCompaction.MinCompactionSavingsTokens);

            // Once converged, further rounds must stay free no-ops.
            var (settled, settledClient) = await RunAsync(current);
            Assert.False(settled.Compacted);
            Assert.Equal(0, settledClient.CallCount);
            Assert.Equal(TokensOf(current), TokensOf(settled.Session));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task CompactIfNeededAsync_PrefixIsOnlyAPriorSummary_SkipsSummaryCall()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = CreateCompactionSettings();
            var paths = new CompactionTests.TestAppPathProvider(root);
            paths.EnsureCreated();
            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());

            // Shape left behind by a first compaction: [summary, user, tail]. The protected tail plus
            // the cap leave the prior summary alone in the prefix, so the only possible pass would
            // condense a summary into a summary — the exact loop that used to keep token counts flat.
            // The idle guard must refuse to pay for it.
            var between = new List<ChatMessage>
            {
                SummaryMessageBuilder.CreateSummaryPlaceholder("earlier summary", null),
                ChatMessage.Create(MessageRole.User, "do the task")
            };
            AppendToolPairs(between, pairs: 9, startingIndex: 0);
            Assert.True(between.Count <= settings.ContextCompaction.ProtectedTailMaxMessages + 1);

            var session = AgentSession.Create("summary-only-prefix").WithMessages(between);
            var client = new CountingSummaryClient("summary of a summary");

            var result = await new ConversationCompactor(
                settings,
                client,
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger()).CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(CompactionKind.ConversationCompact, Force: false, EmitAudit: true));

            Assert.False(result.Compacted);
            Assert.Equal(0, client.CallCount);
            Assert.Equal(between.Select(message => message.Id), result.Session.Messages.Select(message => message.Id));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task CompactIfNeededAsync_ProjectedSavingsBelowMinimum_SkipsSummaryCall()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = CreateCompactionSettings();
            // Demand more savings than the payload can possibly yield, so the guard must trip before
            // any summary round trip is paid for.
            settings.ContextCompaction.MinCompactionSavingsTokens = 10_000_000;
            var paths = new CompactionTests.TestAppPathProvider(root);
            paths.EnsureCreated();
            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var client = new CountingSummaryClient("summary");
            var session = AgentSession.Create("idle-guard").WithMessages(BuildLongTurn(pairs: 30));
            var originalIds = session.Messages.Select(message => message.Id).ToArray();

            var result = await new ConversationCompactor(
                settings,
                client,
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger()).CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(CompactionKind.ConversationCompact, Force: false, EmitAudit: true));

            Assert.False(result.Compacted);
            Assert.Equal(0, client.CallCount);
            Assert.Equal(originalIds, result.Session.Messages.Select(message => message.Id).ToArray());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---------- prompt pressure / budget resolution ----------

    [Fact]
    public void ApplyPromptPressure_PreferMeasured_LowersUtilizationToMeasuredValue()
    {
        var budget = new ContextBudgetSnapshot(200_000, 0, 20_000, 180_000, 50_000, 0.28);

        var resolved = ContextBudgetResolver.ApplyPromptPressure(budget, lastPromptTokens: 40_000, preferMeasured: true);

        Assert.Equal(20_000, resolved.EstimatedHistory);
        // Utilization now equals the measured prompt over the usable window, independent of the
        // FixedOverhead estimate: (20_000 + 20_000) / 200_000.
        Assert.Equal(0.2, resolved.TotalUtilization, 6);
    }

    [Fact]
    public void ApplyPromptPressure_FloorMode_KeepsEstimateWhenMeasurementIsLower()
    {
        var budget = new ContextBudgetSnapshot(200_000, 0, 20_000, 180_000, 50_000, 0.28);

        var resolved = ContextBudgetResolver.ApplyPromptPressure(budget, lastPromptTokens: 40_000, preferMeasured: false);

        Assert.Equal(50_000, resolved.EstimatedHistory);
        Assert.Equal(budget.TotalUtilization, resolved.TotalUtilization, 6);
    }

    [Fact]
    public void ApplyPromptPressure_FloorMode_RaisesEstimateWhenMeasurementIsHigher()
    {
        var budget = new ContextBudgetSnapshot(200_000, 0, 20_000, 180_000, 50_000, 0.28);

        var resolved = ContextBudgetResolver.ApplyPromptPressure(budget, lastPromptTokens: 90_000, preferMeasured: false);

        Assert.Equal(70_000, resolved.EstimatedHistory);
    }

    [Fact]
    public void ApplyPromptPressure_WithoutMeasurement_ReturnsBudgetUnchanged()
    {
        var budget = new ContextBudgetSnapshot(200_000, 0, 20_000, 180_000, 50_000, 0.28);

        Assert.Equal(budget, ContextBudgetResolver.ApplyPromptPressure(budget, null));
        Assert.Equal(budget, ContextBudgetResolver.ApplyPromptPressure(budget, 0));
    }

    /// <summary>
    /// The ratchet that kept the meter high: a stale measurement from before compaction used to be
    /// folded back into the post-compaction budget. Clearing the store on compaction, then resolving
    /// again, must not raise utilization back up.
    /// </summary>
    [Fact]
    public void ApplyPromptPressure_AfterClear_UsesEstimateSoUtilizationDrops()
    {
        var preCompaction = new ContextBudgetSnapshot(200_000, 0, 20_000, 180_000, 160_000, 0.9);
        var store = new PromptPressureStore();
        store.Record("s1", 175_000);

        var beforeCompaction = ContextBudgetResolver.ApplyPromptPressure(
            preCompaction, store.GetLastPromptTokens("s1"));
        Assert.Equal(0.875, beforeCompaction.TotalUtilization, 6);

        // Compaction rewrote the payload: the stored 175k no longer describes the request.
        store.Clear("s1");
        var shrunkPayload = new ContextBudgetSnapshot(200_000, 0, 20_000, 180_000, 30_000, 0.25);
        var afterCompaction = ContextBudgetResolver.ApplyPromptPressure(
            shrunkPayload, store.GetLastPromptTokens("s1"));

        Assert.Null(store.GetLastPromptTokens("s1"));
        Assert.Equal(0.25, afterCompaction.TotalUtilization, 6);
        Assert.True(afterCompaction.TotalUtilization < beforeCompaction.TotalUtilization);
    }

    [Fact]
    public void Clear_UnknownOrBlankSession_IsANoOp()
    {
        var store = new PromptPressureStore();
        store.Record("s1", 1_000);

        store.Clear("other");
        store.Clear(string.Empty);
        store.Clear("  ");

        Assert.Equal(1_000, store.GetLastPromptTokens("s1"));
    }

    [Fact]
    public void Resolve_AppliesCalibrationAndMeasuredTokensTogether()
    {
        var settings = new ContextCompactionSettings { ContextWindowTokens = 100_000 };
        var messages = new[] { ChatMessage.Create(MessageRole.User, new string('x', 2_500)) };

        var uncalibrated = ContextBudgetResolver.Resolve(
            "prompt",
            [],
            messages,
            settings,
            new ModelSettings { MaxTokens = 1_000 });

        var calibrated = ContextBudgetResolver.Resolve(
            "prompt",
            [],
            messages,
            settings,
            new ModelSettings { MaxTokens = 1_000 },
            calibrationMultiplier: 2.0);

        Assert.True(calibrated.EstimatedHistory > uncalibrated.EstimatedHistory);

        var measured = ContextBudgetResolver.Resolve(
            "prompt",
            [],
            messages,
            settings,
            new ModelSettings { MaxTokens = 1_000 },
            lastPromptTokens: 60_000);

        Assert.Equal(60_000 - measured.FixedOverhead, measured.EstimatedHistory);
        Assert.Equal(60_000, measured.EstimatedTotalPrompt);
    }

    // ---------- helpers ----------

    private static ContextCompactionSettings CreateSemanticSettings() =>
        new()
        {
            ProtectedTailMaxMessages = ProtectedTailMessages,
            ReattachLatestUserMessage = true,
            DynamicCompaction = new DynamicCompactionSettings
            {
                Enabled = true,
                EnableSemanticCutoff = true
            }
        };

    private static AppSettings CreateCompactionSettings() =>
        new()
        {
            ContextCompaction = new ContextCompactionSettings
            {
                Enabled = true,
                TriggerMessages = 10,
                KeepMessages = ProtectedTailMessages,
                ProtectedTailMaxMessages = ProtectedTailMessages,
                MinCompactionSavingsTokens = 2_000,
                OffloadBeforeCompact = false,
                MaxConversationCharsForSummary = 1_000_000,
                DynamicCompaction = new DynamicCompactionSettings
                {
                    Enabled = true,
                    EnableSemanticCutoff = true
                }
            },
            Model = new ModelSettings { MaxTokens = 1_000 }
        };

    /// <summary>
    /// One turn: a single user instruction followed by many assistant/tool rounds, mirroring the
    /// shape that used to be unsummarizable.
    /// </summary>
    private static List<ChatMessage> BuildLongTurn(int pairs)
    {
        var messages = new List<ChatMessage> { ChatMessage.Create(MessageRole.User, "do the task") };
        AppendToolPairs(messages, pairs, startingIndex: 0);
        return messages;
    }

    private static void AppendToolPairs(List<ChatMessage> messages, int pairs, int startingIndex)
    {
        for (var i = 0; i < pairs; i++)
        {
            var id = $"c{startingIndex + i}";
            messages.Add(ChatMessage.Create(
                MessageRole.Assistant,
                $"step-{i}",
                toolCalls: [new AgentToolCall(id, "file_read", new Dictionary<string, string>())]));
            messages.Add(ChatMessage.Create(
                MessageRole.Tool,
                $"ToolCallId: {id}\ntool-output-{i}{new string('.', 4_000)}"));
        }
    }

    private static int TokensOf(AgentSession session) =>
        ContextTokenEstimator.Estimate(
            ConversationMessageFilters.WithoutCompactionAudits(session.Messages),
            includeReasoningInModelContext: false);

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "athlon-compaction-rework", Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class CountingSummaryClient(string summary) : IAgentModelClient
    {
        public int CallCount { get; private set; }

        public string? LastPrompt { get; private set; }

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPrompt = string.Join(
                "\n",
                request.Messages.Select(message => message.Content as string ?? string.Empty));
            return Task.FromResult(new AgentModelResponse(summary, Array.Empty<AgentToolCall>()));
        }
    }

    private sealed class CapturingSummaryClient(string summary) : IAgentModelClient
    {
        public string LastPrompt { get; private set; } = string.Empty;

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join(
                "\n",
                request.Messages.Select(message => message.Content as string ?? string.Empty));
            return Task.FromResult(new AgentModelResponse(summary, Array.Empty<AgentToolCall>()));
        }
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
