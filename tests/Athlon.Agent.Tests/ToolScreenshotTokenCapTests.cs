using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class ToolScreenshotTokenCapTests
{
    [Fact]
    public void Estimate_CapsToolScreenshots_ToNewestN()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "start")
        };
        AppendToolScreenshot(history, "a");
        AppendToolScreenshot(history, "b");
        AppendToolScreenshot(history, "c");
        AppendToolScreenshot(history, "d");
        AppendToolScreenshot(history, "e");

        var uncapped = ContextTokenEstimator.Estimate(history, maxToolScreenshots: int.MaxValue);
        var capped = ContextTokenEstimator.Estimate(history, maxToolScreenshots: 2);

        Assert.Equal(uncapped - (3 * 900), capped);
    }

    [Fact]
    public void Estimate_DoesNotCapUserUploadedImages()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(
                MessageRole.User,
                "reference",
                imageAttachments:
                [
                    new ImageAttachment("u1.png", "image/png", DataUrl: "data:image/png;base64,AA"),
                    new ImageAttachment("u2.png", "image/png", DataUrl: "data:image/png;base64,AQ")
                ])
        };
        AppendToolScreenshot(history, "t1");
        AppendToolScreenshot(history, "t2");
        AppendToolScreenshot(history, "t3");

        var withCap = ContextTokenEstimator.Estimate(history, maxToolScreenshots: 2);
        var toolOnlyCapped = ContextTokenEstimator.Estimate(
            history.Where(message => message.Role != MessageRole.User).ToArray(),
            maxToolScreenshots: 2);
        var userOnly = ContextTokenEstimator.Estimate(
            history.Where(message => message.Role == MessageRole.User).ToArray(),
            maxToolScreenshots: 0);

        Assert.Equal(userOnly + toolOnlyCapped, withCap);
        Assert.True(userOnly >= 1800);
    }

    [Fact]
    public void ContextBudgetCalculator_UsesConfiguredScreenshotCap()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "start")
        };
        AppendToolScreenshot(history, "a");
        AppendToolScreenshot(history, "b");
        AppendToolScreenshot(history, "c");

        var settings = new ContextCompactionSettings
        {
            MaxToolScreenshotsInModelContext = 1,
            ContextWindowTokens = 100_000
        };
        var budget = ContextBudgetCalculator.Compute(
            "system",
            [],
            history,
            settings,
            new ModelSettings());

        var expected = ContextTokenEstimator.Estimate(
            history,
            settings.IncludeReasoningInModelContext,
            maxToolScreenshots: 1);
        Assert.Equal(expected, budget.EstimatedHistory);
    }

    private static void AppendToolScreenshot(List<ChatMessage> history, string id)
    {
        var call = new AgentToolCall(id, "computer_observe", ToolCallArguments.Empty);
        history.Add(ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: [call]));
        history.Add(ChatMessage.Create(
            MessageRole.Tool,
            AgentRuntime.FormatToolResult(call, ToolResult.Success("ok", "{}")),
            imageAttachments:
            [
                new ImageAttachment($"{id}.png", "image/png", DataUrl: $"data:image/png;base64,{id}")
            ]));
    }
}
