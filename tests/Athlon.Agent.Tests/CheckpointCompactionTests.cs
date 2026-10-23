using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class CheckpointCompactionTests
{
    [Fact]
    public void TrySplit_RetainsRecentUsersWithinTokenBudget()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(ChatMessage.Create(MessageRole.User, $"user-{i}-" + new string('x', 400)));
            messages.Add(ChatMessage.Create(MessageRole.Assistant, $"ok-{i}"));
        }

        var split = CheckpointHistoryBuilder.TrySplit(messages, retainUserTokenBudget: 200);
        Assert.NotNull(split);
        Assert.NotEmpty(split!.PrefixToSummarize);
        Assert.NotEmpty(split.RetainedUsers);
        Assert.All(split.RetainedUsers, m => Assert.Equal(MessageRole.User, m.Role));
        Assert.True(split.SummarizedEndExclusive > 0);
        Assert.True(split.SummarizedEndExclusive < messages.Count);
    }

    [Fact]
    public void TrySplit_KeepsPairingTailWhenToolCallsOpen()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "old task"),
            ChatMessage.Create(MessageRole.Assistant, "done"),
            ChatMessage.Create(MessageRole.User, "new task"),
            ChatMessage.Create(
                MessageRole.Assistant,
                string.Empty,
                toolCalls: [new AgentToolCall("c1", "file_read", new Dictionary<string, string> { ["path"] = "a.txt" })]),
            ChatMessage.Create(MessageRole.Tool, "ToolCallId: c1\ncontent")
        };

        var split = CheckpointHistoryBuilder.TrySplit(messages, retainUserTokenBudget: 50_000);
        Assert.NotNull(split);
        Assert.Contains(split!.RetainedUsers, m => m.Content!.Contains("new task", StringComparison.Ordinal));
        // Open tool group at end must remain pairing-complete in retained suffix.
        var retained = split.RetainedUsers.Concat(split.PairingTail).ToList();
        Assert.Contains(retained, m => m.Role == MessageRole.Assistant);
        Assert.Contains(retained, m => m.Role == MessageRole.Tool);
    }

    [Fact]
    public void ResolveRetainUserTokenBudget_OverflowUsesTighterCap()
    {
        var settings = new ContextCompactionSettings
        {
            RetainRecentUserMaxTokens = 20_000,
            OverflowRetainRecentUserMaxTokens = 8_000
        };

        Assert.Equal(
            20_000,
            CheckpointHistoryBuilder.ResolveRetainUserTokenBudget(settings, ContextPressureLevel.Critical, force: false));
        Assert.Equal(
            8_000,
            CheckpointHistoryBuilder.ResolveRetainUserTokenBudget(settings, ContextPressureLevel.Overflow, force: false));
        Assert.Equal(
            8_000,
            CheckpointHistoryBuilder.ResolveRetainUserTokenBudget(settings, ContextPressureLevel.Critical, force: true));
    }

    [Fact]
    public void DynamicCompactionPlan_OnlyCheckpoints_NoPruneFlags()
    {
        var settings = new ContextCompactionSettings
        {
            DynamicCompaction = new DynamicCompactionSettings { Enabled = true, TargetUtilization = 0.80 }
        };
        var conversation = new[] { ChatMessage.Create(MessageRole.User, "hi") };
        var budget = new ContextBudgetSnapshot(100_000, 8_000, 10_000, 70_000, 80_000, 0.9);
        var plan = DynamicCompactionPlan.Create(
            ContextPressureLevel.Critical,
            budget,
            conversation,
            settings,
            force: false);

        Assert.True(plan.ApplyConversationCompact);
        Assert.Equal(settings.RetainRecentUserMaxTokens, plan.RetainUserTokenBudget);
    }

    [Fact]
    public void TemplateSummaryBuilder_EmitsCheckpointSections()
    {
        var prefix = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "finish migration"),
            ChatMessage.Create(MessageRole.Assistant, "working")
        };
        var summary = TemplateSummaryBuilder.Build(prefix, new ContextCompactionSettings(), null);
        Assert.Contains("CONTEXT CHECKPOINT", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SESSION INTENT", summary, StringComparison.Ordinal);
        Assert.Contains("finish migration", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void IsFixedOverheadDominating_WhenOverheadConsumesWindow()
    {
        var budget = new ContextBudgetSnapshot(10_000, 1_000, 8_000, 500, 500, 1.0);
        Assert.True(ContextPressureEvaluator.IsFixedOverheadDominating(
            budget,
            new ContextCompactionSettings { FixedOverheadDominatesRatio = 0.85 }));
    }
}
