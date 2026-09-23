using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.Tests;

/// <summary>
/// Computer Use observations are large but expire quickly, so they get a tighter history budget
/// than the global 8000-token allowance. Hygiene and the token estimator must agree on that
/// budget, otherwise the context budget is computed against text that is never actually sent.
/// </summary>
public sealed class ComputerUseHistoryHygieneTests
{
    [Fact]
    public void ResolveToolResultLimit_UsesOverrideForConfiguredTool()
    {
        var settings = new RequestHistoryHygieneSettings();
        settings.ToolResultOverrides["computer_observe"] = new ToolResultLimit(120, 12_000, 3_000);

        var limit = settings.ResolveToolResultLimit("computer_observe");

        Assert.Equal(120, limit.MaxLines);
        Assert.Equal(12_000, limit.MaxBytes);
        Assert.Equal(3_000, limit.MaxTokens);
    }

    [Fact]
    public void ResolveToolResultLimit_FallsBackForUnknownTool()
    {
        var settings = new RequestHistoryHygieneSettings();
        settings.ToolResultOverrides["computer_observe"] = new ToolResultLimit(120, 12_000, 3_000);

        var limit = settings.ResolveToolResultLimit("execute_command");

        Assert.Equal(settings.MaxToolResultLines, limit.MaxLines);
        Assert.Equal(settings.MaxToolResultBytes, limit.MaxBytes);
        Assert.Equal(settings.MaxToolResultTokens, limit.MaxTokens);
    }

    [Fact]
    public void ResolveToolResultLimit_NonPositiveOverrideValues_UseGlobal()
    {
        var settings = new RequestHistoryHygieneSettings();
        // A partially specified override must not collapse unspecified dimensions to zero.
        settings.ToolResultOverrides["computer_observe"] = new ToolResultLimit(0, 0, 500);

        var limit = settings.ResolveToolResultLimit("computer_observe");

        Assert.Equal(settings.MaxToolResultLines, limit.MaxLines);
        Assert.Equal(settings.MaxToolResultBytes, limit.MaxBytes);
        Assert.Equal(500, limit.MaxTokens);
    }

    [Fact]
    public void DefaultCompactionSettings_SeedComputerUseOverrides()
    {
        var settings = new ContextCompactionSettings();
        var hygiene = settings.RequestHistoryHygiene;

        foreach (var toolName in ComputerUseToolNames.All)
        {
            Assert.True(
                hygiene.ToolResultOverrides.ContainsKey(toolName),
                $"Expected a default override for {toolName}.");
        }

        var observe = hygiene.ResolveToolResultLimit(ComputerUseToolNames.Observe);
        Assert.Equal(ComputerUseSettingsDefaults.ObservationResultMaxTokens, observe.MaxTokens);
        Assert.True(observe.MaxTokens < hygiene.MaxToolResultTokens);
    }

    [Fact]
    public void ApplyToModelMessages_AppliesTighterBudgetToComputerUseResult()
    {
        var settings = new ContextCompactionSettings().RequestHistoryHygiene;
        // Comfortably under the global 8000-token limit but over the Computer Use 3000-token budget.
        var body = string.Join('\n', Enumerable.Range(1, 400).Select(index => $"ui_{index} Button ok"));
        var messages = BuildToolExchange("computer_observe", body);

        var result = RequestHistoryHygiene.ApplyToModelMessages(messages, settings);

        var text = Assert.IsType<string>(result.Messages[^1].Content);
        Assert.Contains("cache hygiene", text, StringComparison.Ordinal);
        Assert.True(result.EstimatedSavingsTokens > 0);
    }

    [Fact]
    public void ApplyToModelMessages_LeavesIdenticalPayloadUnderGlobalLimitUntouched()
    {
        var global = new ContextCompactionSettings().RequestHistoryHygiene;
        var body = string.Join('\n', Enumerable.Range(1, 400).Select(index => $"line {index} ok"));
        // Same size as the Computer Use payload above, but a non-Computer-Use tool keeps the
        // global budget and therefore stays intact.
        var messages = BuildToolExchange("execute_command", body);

        var result = RequestHistoryHygiene.ApplyToModelMessages(messages, global);

        var text = Assert.IsType<string>(result.Messages[^1].Content);
        Assert.DoesNotContain("cache hygiene", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Estimator_ClampsComputerUseResultLikeHygiene()
    {
        var settings = new ContextCompactionSettings();
        var hygiene = settings.RequestHistoryHygiene;
        var body = string.Join('\n', Enumerable.Range(1, 400).Select(index => $"ui_{index} Button ok"));
        var messages = ToChatMessages(BuildToolExchange("computer_observe", body));

        var estimated = ContextTokenEstimator.Estimate(messages, hygiene: hygiene);
        var unlimited = ContextTokenEstimator.Estimate(
            messages,
            hygiene: new RequestHistoryHygieneSettings { Enabled = false });

        // The tightened Computer Use budget must reduce the estimate relative to no hygiene.
        Assert.True(estimated < unlimited);

        var rawTokens = ContextTokenEstimator.EstimateTextTokens(
            Assert.IsType<string>(BuildToolExchange("computer_observe", body)[^1].Content));
        Assert.True(rawTokens > ComputerUseSettingsDefaults.ObservationResultMaxTokens);
    }

    [Fact]
    public void StripUiTree_ReplacesTreeWithSummary()
    {
        var payload = """
            ToolCallId: call-1
            Tool `computer_observe` succeeded.

            Arguments: {}
            Summary: Desktop observed

            {
              "frame_id": "frame_abc",
              "ui_tree": [
              {"element_id":"ui_1","name":"Window"},
              {"element_id":"ui_2","name":"Button"}
              ]
            }
            """;

        var stripped = RequestHistoryHygiene.StripUiTree(payload);

        Assert.DoesNotContain("ui_1", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("ui_2", stripped, StringComparison.Ordinal);
        Assert.Contains("ui_tree stripped from history", stripped, StringComparison.Ordinal);
        Assert.Contains("2 node(s)", stripped, StringComparison.Ordinal);
        // Non-tree context survives so older turns stay interpretable.
        Assert.Contains("frame_abc", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void StripUiTree_HandlesBracesInsideNodeNames()
    {
        var payload = """{"frame_id":"f","ui_tree":[{"element_id":"ui_1","name":"weird ] } name"}]}""";

        var stripped = RequestHistoryHygiene.StripUiTree(payload);

        Assert.Contains("1 node(s)", stripped, StringComparison.Ordinal);
        Assert.EndsWith("}", stripped);
    }

    [Fact]
    public void StripUiTree_LeavesPayloadWithoutTreeUntouched()
    {
        const string payload = """{"frame_id":"f","desktop":{"left":0}}""";

        Assert.Equal(payload, RequestHistoryHygiene.StripUiTree(payload));
    }

    [Fact]
    public void ApplyToModelMessages_KeepsNewestUiTreesWhenPruningEnabled()
    {
        var settings = new RequestHistoryHygieneSettings
        {
            PruneHistoricalUiTree = true,
            HistoryUiTreeRetention = 1
        };
        var messages = new List<AgentModelMessage>
        {
            new("system", "sys")
        };
        messages.AddRange(BuildObserveExchange("call-1", "frame_old"));
        messages.AddRange(BuildObserveExchange("call-2", "frame_new"));

        var result = RequestHistoryHygiene.ApplyToModelMessages(messages, settings);

        var oldContent = Assert.IsType<string>(result.Messages[2].Content);
        var newContent = Assert.IsType<string>(result.Messages[4].Content);
        Assert.Contains("ui_tree stripped from history", oldContent, StringComparison.Ordinal);
        // The newest observation keeps its full tree.
        Assert.Contains("\"element_id\"", newContent, StringComparison.Ordinal);
        Assert.DoesNotContain("stripped", newContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyToModelMessages_PruningDisabledByDefault()
    {
        var settings = new RequestHistoryHygieneSettings { HistoryUiTreeRetention = 0 };
        var messages = new List<AgentModelMessage>
        {
            new("system", "sys")
        };
        messages.AddRange(BuildObserveExchange("call-1", "frame_old"));
        messages.AddRange(BuildObserveExchange("call-2", "frame_new"));

        var result = RequestHistoryHygiene.ApplyToModelMessages(messages, settings);

        Assert.False(settings.PruneHistoricalUiTree);
        var oldContent = Assert.IsType<string>(result.Messages[2].Content);
        Assert.DoesNotContain("ui_tree stripped from history", oldContent, StringComparison.Ordinal);
    }

    private static List<AgentModelMessage> BuildObserveExchange(string callId, string frameId)
    {
        var body = $$"""
            {
              "frame_id": "{{frameId}}",
              "ui_tree": [{"element_id":"ui_1","name":"Window"}]
            }
            """;
        var call = new AgentToolCall(callId, ComputerUseToolNames.Observe, new Dictionary<string, string>());
        return
        [
            new("assistant", string.Empty, ToolCalls: [call]),
            new("tool", ModelMessageBuilder.FormatToolResult(call, ToolResult.Success("ok", body)), callId)
        ];
    }

    private static List<AgentModelMessage> BuildToolExchange(string toolName, string body) =>
    [
        new("system", "sys"),
        new(
            "assistant",
            string.Empty,
            ToolCalls: [new AgentToolCall("call-1", toolName, new Dictionary<string, string>())]),
        new(
            "tool",
            AgentRuntime.FormatToolResult(
                new AgentToolCall("call-1", toolName, new Dictionary<string, string>()),
                ToolResult.Success("ok", body)),
            "call-1")
    ];

    private static List<ChatMessage> ToChatMessages(IReadOnlyList<AgentModelMessage> modelMessages)
    {
        var messages = new List<ChatMessage>();
        foreach (var message in modelMessages)
        {
            var role = message.Role switch
            {
                "system" => MessageRole.System,
                "assistant" => MessageRole.Assistant,
                "tool" => MessageRole.Tool,
                _ => MessageRole.User
            };
            messages.Add(ChatMessage.CreateWithId(
                Guid.NewGuid().ToString("N"),
                role,
                message.Content as string ?? string.Empty,
                toolCalls: message.ToolCalls));
        }

        return messages;
    }
}
