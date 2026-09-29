using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

public sealed class TruncateArgsServiceTests
{
    [Fact]
    public void ApplyIfNeeded_TruncatesArgumentsOutsideKeepWindow()
    {
        var settings = new ContextCompactionSettings
        {
            TruncateArgs = new TruncateArgsSettings
            {
                TriggerMessages = 2,
                KeepMessages = 1,
                MaxArgLength = 50
            }
        };

        var longArg = new string('x', 200);
        var oldAssistant = ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: new[] { new AgentToolCall("old", "execute_command", new Dictionary<string, string> { ["command"] = longArg }) });
        var recent = ChatMessage.Create(MessageRole.User, "recent");

        var session = AgentSession.Create("truncate")
            .WithMessages(new[] { oldAssistant, recent });

        var service = new TruncateArgsService();
        var result = service.ApplyIfNeeded(session, settings);

        var updatedCalls = AssistantToolCallsCodec.Deserialize(result.Messages[0].ToolCallsJson);
        Assert.NotNull(updatedCalls);
        Assert.Contains("...(argument truncated)", updatedCalls![0].Arguments.GetString("command"), StringComparison.Ordinal);
        Assert.DoesNotContain(longArg, updatedCalls[0].Arguments.GetString("command"), StringComparison.Ordinal);
        Assert.Equal("recent", result.Messages[1].Content);
    }

    [Fact]
    public void ApplyIfNeeded_DoesNotModifyTailWindow()
    {
        var settings = new ContextCompactionSettings
        {
            TruncateArgs = new TruncateArgsSettings
            {
                TriggerMessages = 2,
                KeepMessages = 1,
                MaxArgLength = 50
            }
        };

        var longArg = new string('y', 200);
        var recentAssistant = ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: new[] { new AgentToolCall("recent", "execute_command", new Dictionary<string, string> { ["command"] = longArg }) });
        var older = ChatMessage.Create(MessageRole.User, "older");

        var session = AgentSession.Create("truncate-tail")
            .WithMessages(new[] { older, recentAssistant });

        var service = new TruncateArgsService();
        var result = service.ApplyIfNeeded(session, settings);

        var updatedCalls = AssistantToolCallsCodec.Deserialize(result.Messages[1].ToolCallsJson);
        Assert.NotNull(updatedCalls);
        Assert.Equal(longArg, updatedCalls![0].Arguments.GetString("command"));
    }

    [Fact]
    public void ApplyIfNeeded_KeepsPublishPlanBody()
    {
        var settings = new ContextCompactionSettings
        {
            TruncateArgs = new TruncateArgsSettings
            {
                TriggerMessages = 2,
                KeepMessages = 1,
                MaxArgLength = 40
            }
        };

        var body = new string('p', 400);
        var oldAssistant = ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: [new AgentToolCall("plan", "publish_plan", new Dictionary<string, string> { ["body"] = body, ["title"] = new string('t', 80) })]);
        var recent = ChatMessage.Create(MessageRole.User, "recent");
        var session = AgentSession.Create("truncate-body").WithMessages([oldAssistant, recent]);

        var result = new TruncateArgsService().ApplyIfNeeded(session, settings);
        var updatedCalls = AssistantToolCallsCodec.Deserialize(result.Messages[0].ToolCallsJson);

        Assert.NotNull(updatedCalls);
        Assert.Equal(body, updatedCalls![0].Arguments.GetString("body"));
        Assert.Contains("...(argument truncated)", updatedCalls[0].Arguments.GetString("title"), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIfNeeded_TruncatesPublishPlanBodyAfterApprovedPlan()
    {
        var settings = new ContextCompactionSettings
        {
            TruncateArgs = new TruncateArgsSettings
            {
                TriggerMessages = 2,
                KeepMessages = 1,
                MaxArgLength = 40
            }
        };

        var body = new string('p', 400);
        var oldAssistant = ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: [new AgentToolCall("plan", "publish_plan", new Dictionary<string, string> { ["body"] = body, ["title"] = "title" })]);
        var approved = ChatMessage.Create(MessageRole.User, ApprovedPlanPrompt.BuildUserMessage("ship it"));
        var recent = ChatMessage.Create(MessageRole.User, "recent");
        var session = AgentSession.Create("truncate-body-after-approval")
            .WithMessages([oldAssistant, approved, recent]);

        var result = new TruncateArgsService().ApplyIfNeeded(session, settings);
        var updatedCalls = AssistantToolCallsCodec.Deserialize(result.Messages[0].ToolCallsJson);

        Assert.NotNull(updatedCalls);
        Assert.Contains("...(argument truncated)", updatedCalls![0].Arguments.GetString("body"), StringComparison.Ordinal);
    }
}
