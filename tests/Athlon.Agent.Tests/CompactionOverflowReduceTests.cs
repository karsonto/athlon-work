using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class CompactionOverflowReduceTests
{
    [Fact]
    public void ResolveKeepTokenBudget_Overflow_DoesNotInflateWithStaticKeepMessages()
    {
        var settings = new ContextCompactionSettings
        {
            KeepMessages = 40,
            KeepTokens = 0,
            DynamicCompaction = new DynamicCompactionSettings
            {
                Enabled = true,
                PostCompactionUtilization = 0.45,
                OverflowPostCompactionUtilization = 0.20
            }
        };

        var conversation = BuildToolHeavyConversation(userText: "do the task", toolRounds: 30);
        // TotalWindow 32000, Reserved 4000 → usable 28000; FixedOverhead 5000; overflow target 0.2
        // dynamicKeep = floor(0.2*28000 - 5000) = 600
        var budget = new ContextBudgetSnapshot(
            TotalWindow: 32_000,
            ReservedOutput: 4_000,
            FixedOverhead: 5_000,
            HistoryBudget: 20_000,
            EstimatedHistory: 20_000,
            HistoryUtilization: 1.0);

        var keep = ContextPressureEvaluator.ResolveKeepTokenBudget(
            budget,
            ContextPressureLevel.Overflow,
            conversation,
            settings,
            includesConversationCompact: true);

        var staticKeep = ContextTokenEstimator.EstimateSuffix(
            conversation,
            Math.Max(0, conversation.Count - 40));

        Assert.True(keep < staticKeep, $"overflow keep {keep} should be below static keep ~{staticKeep}");
        Assert.True(keep >= 512, $"overflow keep {keep} should respect floor");
    }

    [Fact]
    public void IsFixedOverheadDominating_WhenOverheadConsumesWindow()
    {
        var settings = new ContextCompactionSettings { FixedOverheadDominatesRatio = 0.85 };
        var budget = new ContextBudgetSnapshot(
            TotalWindow: 10_000,
            ReservedOutput: 1_000,
            FixedOverhead: 8_000,
            HistoryBudget: 500,
            EstimatedHistory: 500,
            HistoryUtilization: 1.0);

        Assert.True(ContextPressureEvaluator.IsFixedOverheadDominating(budget, settings));
    }

    [Fact]
    public void TemplateSummaryBuilder_RespectsHardCap()
    {
        var settings = new ContextCompactionSettings
        {
            SummaryMaxTokens = 200,
            OverflowReattachUserMaxChars = 500
        };
        var prefix = BuildToolHeavyConversation("huge user " + new string('x', 8_000), toolRounds: 20);
        var appendix = "<must_preserve>\n" + new string('y', 4_000) + "\n</must_preserve>";
        var summary = TemplateSummaryBuilder.Build(prefix, settings, appendix);
        Assert.Contains("template fallback", summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(summary.Length < 8_000, $"template length {summary.Length} should be capped");
    }

    [Fact]
    public void CutoffDegrader_ProducesPositiveCutoff_AndKeepsToolPairs()
    {
        var settings = new ContextCompactionSettings { OverflowProtectedTailMaxMessages = 6 };
        var conversation = BuildToolHeavyConversation("start", toolRounds: 8);
        var degraded = CompactionCutoffDegrader.TryDegrade(conversation, settings);
        Assert.NotNull(degraded);
        Assert.True(degraded.Value.Plan.SummarizedEnd > 0);
        Assert.True(degraded.Value.Plan.SummarizedEnd < conversation.Count);

        var rewritten = degraded.Value.Conversation;
        AssertNoOrphanToolCalls(rewritten);
    }

    [Fact]
    public void ShrinkCutoffToKeepBudget_AdvancesCutoffWhenTailTooLarge()
    {
        var settings = new ContextCompactionSettings();
        var conversation = BuildToolHeavyConversation("task", toolRounds: 25);
        var startCutoff = Math.Max(1, conversation.Count / 4);

        var tight = SemanticCutoffPlanner.ShrinkCutoffToKeepBudget(
            conversation,
            settings,
            summarizedEnd: startCutoff,
            keepTokenBudget: 800,
            summaryMaxTokensHint: 256,
            maxIterations: 5);

        Assert.True(tight >= startCutoff);
        Assert.True(tight < conversation.Count);
    }

    [Fact]
    public void PressurePrune_RetryOutbound_UsesTighterPreviewThanCritical()
    {
        var settings = new ContextCompactionSettings
        {
            ToolResultEviction = new ToolResultEvictionSettings { PreviewChars = 2_000 },
            CriticalPreviewDivisor = 2,
            OverflowPreviewDivisor = 4,
            OverflowRetryPreviewDivisor = 8
        };

        var critical = PressurePrune.ResolvePreviewChars(settings, ContextPressureLevel.Critical, retryOutbound: false);
        var overflow = PressurePrune.ResolvePreviewChars(settings, ContextPressureLevel.Overflow, retryOutbound: false);
        var retry = PressurePrune.ResolvePreviewChars(settings, ContextPressureLevel.Overflow, retryOutbound: true);

        Assert.True(critical > overflow);
        Assert.True(overflow > retry);
    }

    [Fact]
    public void OverflowRetryPrune_OnClone_LeavesSessionMessagesIntact()
    {
        // Mirrors AgentTurnCoordinator.ApplyOutboundOverflowPrune: truncate+reevict on a copy only.
        var settings = new ContextCompactionSettings
        {
            TruncateArgs = new TruncateArgsSettings
            {
                Enabled = true,
                TriggerMessages = 1,
                KeepMessages = 0,
                MaxArgLength = 2_000
            },
            ToolResultEviction = new ToolResultEvictionSettings { PreviewChars = 2_000 },
            OverflowMaxArgLength = 500,
            OverflowRetryPreviewDivisor = 8
        };

        var longArg = new string('x', 4_000);
        var longPreview = new string('p', 3_000);
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: [new AgentToolCall("c1", "execute_command", new Dictionary<string, string> { ["command"] = longArg })]);
        var tool = ChatMessage.Create(
            MessageRole.Tool,
            "[Tool result evicted to disk]\nPreview:\n" + longPreview);
        var session = AgentSession.Create("outbound-prune").WithMessages([assistant, tool]);
        var originalArg = AssistantToolCallsCodec.Deserialize(session.Messages[0].ToolCallsJson)![0]
            .Arguments.GetString("command");
        var originalTool = session.Messages[1].Content;

        var truncate = new TruncateArgsService();
        var messages = truncate.ApplyToMessages(
            session.Messages,
            settings,
            out var truncated,
            keepTokenBudgetOverride: 0,
            maxArgLengthOverride: PressurePrune.ResolveMaxArgLength(settings, ContextPressureLevel.Overflow));
        var (reevicted, reevictedChanged) = PressurePrune.ApplyReEvict(
            messages,
            settings,
            prefixCutoffExclusive: messages.Count,
            ContextPressureLevel.Overflow,
            retryOutbound: true);

        Assert.True(truncated || reevictedChanged);
        Assert.Equal(originalArg, AssistantToolCallsCodec.Deserialize(session.Messages[0].ToolCallsJson)![0]
            .Arguments.GetString("command"));
        Assert.Equal(originalTool, session.Messages[1].Content);
        Assert.NotEqual(session.Messages, reevicted);
    }

    [Fact]
    public void Critical_AfterPruneBelowThreshold_DoesNotNeedCompact()
    {
        // Mirrors PreCompletionPipeline: Critical continues to LLM compact only while still >= target.
        var settings = new ContextCompactionSettings
        {
            DynamicCompaction = new DynamicCompactionSettings
            {
                Enabled = true,
                TargetUtilization = 0.80
            }
        };

        var afterPrune = new ContextBudgetSnapshot(
            TotalWindow: 100_000,
            ReservedOutput: 8_000,
            FixedOverhead: 10_000,
            HistoryBudget: 70_000,
            EstimatedHistory: 40_000,
            HistoryUtilization: 0.5);
        // TotalUtilization = (10k+40k)/92k ≈ 0.54 < 0.80

        var pressure = ContextPressureEvaluator.Evaluate(afterPrune, settings.DynamicCompaction);
        Assert.NotEqual(ContextPressureLevel.Critical, pressure);
        Assert.False(ContextPressureEvaluator.ShouldCompact(
            afterPrune,
            [ChatMessage.Create(MessageRole.User, "hi")],
            settings,
            pressure,
            force: false));
        Assert.True(afterPrune.TotalUtilization < ContextPressureEvaluator.ResolveCompactThreshold(settings.DynamicCompaction));
    }

    [Fact]
    public void CutoffDegrader_ShouldDegrade_DefaultsOverflowOnly()
    {
        var settings = new ContextCompactionSettings
        {
            EnableCutoffDegradeOnOverflow = true,
            EnableCutoffDegradeOnCritical = false
        };

        Assert.True(CompactionCutoffDegrader.ShouldDegrade(settings, ContextPressureLevel.Overflow, force: false));
        Assert.False(CompactionCutoffDegrader.ShouldDegrade(settings, ContextPressureLevel.Critical, force: false));
        Assert.True(CompactionCutoffDegrader.ShouldDegrade(settings, ContextPressureLevel.Critical, force: true));
    }

    private static void AssertNoOrphanToolCalls(IReadOnlyList<ChatMessage> messages)
    {
        var openIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.Assistant)
            {
                foreach (var call in AssistantToolCallsCodec.Deserialize(message.ToolCallsJson) ?? [])
                {
                    openIds.Add(call.Id);
                }
            }
            else if (message.Role == MessageRole.Tool)
            {
                var content = message.Content ?? string.Empty;
                var marker = "ToolCallId:";
                var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var rest = content[(idx + marker.Length)..].TrimStart();
                    var end = rest.IndexOfAny(['\r', '\n', ' ']);
                    var id = end < 0 ? rest : rest[..end];
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        openIds.Remove(id.Trim());
                    }
                }
            }
        }

        Assert.Empty(openIds);
    }

    private static List<ChatMessage> BuildToolHeavyConversation(string userText, int toolRounds)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, userText)
        };

        for (var i = 0; i < toolRounds; i++)
        {
            var callId = $"call-{i}";
            messages.Add(ChatMessage.Create(
                MessageRole.Assistant,
                string.Empty,
                toolCalls: [new AgentToolCall(callId, "file_read", new Dictionary<string, string> { ["path"] = $"f{i}.txt" })]));
            messages.Add(ChatMessage.Create(
                MessageRole.Tool,
                $"ToolCallId: {callId}\n" + new string('a', 400)));
        }

        return messages;
    }
}
