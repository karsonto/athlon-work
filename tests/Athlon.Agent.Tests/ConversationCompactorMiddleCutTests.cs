using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ConversationCompactorMiddleCutTests
{
    [Fact]
    public async Task MiddleCut_strategy_keeps_head_and_tail_with_hidden_summary()
    {
        var session = BuildSession(16);
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                Enabled = true,
                MiddleCutKeepHeadMessages = 2,
                MiddleCutKeepTailMessages = 3
            }
        };

        var sink = new CapturingRuntimeSink();
        var compactor = new ConversationCompactor(
            settings,
            new FixedSummaryModelClient("middle summary"),
            new NoOpStorage(),
            new TruncateArgsService(),
            new NoOpUsageAccumulator(),
            new NoOpLogger(),
            null,
            sink);

        var result = await compactor.CompactIfNeededAsync(
            session,
            new CompactionExecutionRequest(
                Kind: CompactionKind.ConversationCompact,
                Force: true,
                EmitAudit: false,
                Strategy: CompactionStrategy.MiddleCutOnRetrySkipped));

        Assert.True(result.Compacted);
        var messages = ConversationMessageFilters.WithoutCompactionAudits(result.Session.Messages);
        Assert.Equal(2 + 1 + 3, messages.Count);
        Assert.Equal("m-01", messages[0].Content);
        Assert.Equal("m-02", messages[1].Content);
        var summary = messages.Single(m => m.Role == MessageRole.Summary);
        Assert.True(SummaryMessageBuilder.IsHiddenSummaryMessage(summary));
        Assert.Contains("middle summary", summary.Content);
        Assert.Equal("m-14", messages[^3].Content);
        Assert.Equal("m-15", messages[^2].Content);
        Assert.Equal("m-16", messages[^1].Content);
    }

    [Fact]
    public async Task MiddleCut_strategy_emits_middle_cut_diagnostic_event()
    {
        var session = BuildSession(14);
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                Enabled = true,
                MiddleCutKeepHeadMessages = 2,
                MiddleCutKeepTailMessages = 2
            }
        };

        var sink = new CapturingRuntimeSink();
        var compactor = new ConversationCompactor(
            settings,
            new FixedSummaryModelClient("diag summary"),
            new NoOpStorage(),
            new TruncateArgsService(),
            new NoOpUsageAccumulator(),
            new NoOpLogger(),
            null,
            sink);

        _ = await compactor.CompactIfNeededAsync(
            session,
            new CompactionExecutionRequest(
                Kind: CompactionKind.ConversationCompact,
                Force: true,
                EmitAudit: true,
                Strategy: CompactionStrategy.MiddleCutOnRetrySkipped));

        Assert.Contains(sink.Events, evt => evt.errorCode == RuntimeDiagnosticErrorCodes.CompactionMiddleCutApplied);
    }

    [Fact]
    public async Task MiddleCut_strategy_keeps_tool_pairs_whole_at_both_boundaries()
    {
        // 25 messages: one user instruction followed by 12 assistant/tool rounds, so the naive
        // tail start (count - 3 = 22) is the tool result of pair 10, and a head of 2 ends on the
        // assistant turn whose tool_call would be dropped with the middle.
        var session = BuildToolPairSession(12);
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                Enabled = true,
                MiddleCutKeepHeadMessages = 2,
                MiddleCutKeepTailMessages = 3
            }
        };

        var compactor = new ConversationCompactor(
            settings,
            new FixedSummaryModelClient("middle summary"),
            new NoOpStorage(),
            new TruncateArgsService(),
            new NoOpUsageAccumulator(),
            new NoOpLogger(),
            null,
            new CapturingRuntimeSink());

        var result = await compactor.CompactIfNeededAsync(
            session,
            new CompactionExecutionRequest(
                Kind: CompactionKind.ConversationCompact,
                Force: true,
                EmitAudit: false,
                Strategy: CompactionStrategy.MiddleCutOnRetrySkipped));

        Assert.True(result.Compacted);
        var messages = ConversationMessageFilters.WithoutCompactionAudits(result.Session.Messages);
        var summaryIndex = messages.FindIndex(SummaryMessageBuilder.IsSummaryMessage);

        // The head grew from 2 to 3 messages so the first tool pair stays whole in the prefix.
        Assert.Equal(3, summaryIndex);
        Assert.True(ConversationCutoffPlanner.IsPairingBalancedBefore(messages, summaryIndex));

        // The tail now starts on the assistant turn whose tool_call it still carries, instead of on
        // the orphaned tool result at the naive cut.
        var tailStart = summaryIndex + 1;
        Assert.Equal(MessageRole.Assistant, messages[tailStart].Role);
        Assert.Equal(MessageRole.Tool, messages[tailStart + 1].Role);
        Assert.Contains("ToolCallId: c11", messages[tailStart + 1].Content, StringComparison.Ordinal);
        Assert.True(ConversationCutoffPlanner.IsPairingBalancedInRange(messages, tailStart, messages.Count));
    }

    private static AgentSession BuildToolPairSession(int pairs)
    {
        var messages = new List<ChatMessage> { ChatMessage.Create(MessageRole.User, "u-01") };
        for (var i = 0; i < pairs; i++)
        {
            var id = $"c{i}";
            messages.Add(ChatMessage.Create(
                MessageRole.Assistant,
                $"a-{i:D2}",
                toolCalls: [new AgentToolCall(id, "file_read", new Dictionary<string, string>())]));
            messages.Add(ChatMessage.Create(MessageRole.Tool, $"ToolCallId: {id}\nresult-{i:D2}"));
        }

        return new AgentSession("s2", "title", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, messages);
    }

    private static AgentSession BuildSession(int messageCount)
    {
        var messages = Enumerable.Range(1, messageCount)
            .Select(i => ChatMessage.Create(MessageRole.User, $"m-{i:D2}"))
            .ToArray();
        return new AgentSession("s1", "title", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, messages);
    }

    private sealed class FixedSummaryModelClient(string summary) : IAgentModelClient
    {
        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentModelResponse(summary, [], null));
    }

    private sealed class NoOpUsageAccumulator : ISessionUsageAccumulator
    {
        public SessionUsageSnapshot Get(string sessionId) => SessionUsageSnapshot.Empty;
        public SessionUsageSnapshot Record(string sessionId, ModelUsage usage, int contextSavingsTokens = 0) => SessionUsageSnapshot.Empty;
        public SessionUsageSnapshot RecordRollup(string parentSessionId, ModelUsage usage, int hygieneSavingsTokens = 0) => SessionUsageSnapshot.Empty;
        public SessionUsageSnapshot RecordCall(string sessionId, string callId, ModelCallPurpose purpose, ModelUsage usage, int contextSavingsTokens = 0, bool subAgentRollup = false) => SessionUsageSnapshot.Empty;
        public SessionUsageSnapshot RecordCompaction(string sessionId, int tokensBefore, int tokensAfter) => SessionUsageSnapshot.Empty;
        public void Reset(string sessionId) { }
    }

    private sealed class CapturingRuntimeSink : IRuntimeDiagnosticEventSink
    {
        public List<RuntimeDiagnosticEvent> Events { get; } = [];
        public ValueTask EnqueueAsync(RuntimeDiagnosticEvent evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

