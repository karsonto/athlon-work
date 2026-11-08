using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamTraceTranscriptBuilderTests
{
    [Fact]
    public void Flatten_MergesToolCallAndResult_IntoSingleStatus()
    {
        var call = new AgentToolCall(
            "call_1",
            "team_progress",
            ToolCallArguments.FromStrings(new Dictionary<string, string> { ["subtask_ids"] = "" }));
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            "",
            toolCalls: [call]);
        var tool = new ChatMessage(
            ChatMessage.ToolResultMessageId("call_1"),
            MessageRole.Tool,
            "ToolCallId: call_1\nTool `team_progress` succeeded.\nSummary: progress\nArguments: {}",
            DateTimeOffset.UtcNow.AddSeconds(1));

        var items = TeamTraceTranscriptBuilder.FlattenForTests([assistant, tool]);
        var tools = items
            .Select(Serialize)
            .Where(o => string.Equals(o.GetProperty("kind").GetString(), "tool", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(tools);
        Assert.Equal("succeeded", tools[0].GetProperty("status").GetString());
        Assert.Equal("team_progress", tools[0].GetProperty("toolName").GetString());
    }

    [Fact]
    public void Flatten_InFlightCall_ShowsRunningOnce()
    {
        var call = new AgentToolCall(
            "call_2",
            "team_memory",
            ToolCallArguments.Empty);
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            "",
            toolCalls: [call]);

        var items = TeamTraceTranscriptBuilder.FlattenForTests([assistant]);
        var tools = items
            .Select(Serialize)
            .Where(o => string.Equals(o.GetProperty("kind").GetString(), "tool", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(tools);
        Assert.Equal("running", tools[0].GetProperty("status").GetString());
    }

    private static JsonElement Serialize(object item) =>
        JsonSerializer.SerializeToElement(item);
}
