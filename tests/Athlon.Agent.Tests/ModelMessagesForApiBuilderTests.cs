using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class ModelMessagesForApiBuilderTests
{
    [Fact]
    public void Build_applies_hygiene_to_oversized_tool_result()
    {
        var huge = new string('x', 50_000);
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "read file"),
            ChatMessage.CreateWithId(
                "a1",
                MessageRole.Assistant,
                string.Empty,
                null,
                [new AgentToolCall("tc1", "file_read", new Dictionary<string, string>())]),
            ChatMessage.Create(MessageRole.Tool, AgentRuntime.FormatToolResult(
                new AgentToolCall("tc1", "file_read", new Dictionary<string, string>()),
                ToolResult.Success("ok", huge)))
        };

        var result = ModelMessagesForApiBuilder.Build(
            cache: null,
            "system",
            history,
            new ContextCompactionSettings());

        var toolMessage = result.Messages.First(message => message.Role == "tool");
        Assert.Contains("[cache hygiene:", toolMessage.Content, StringComparison.Ordinal);
        Assert.True(result.EstimatedSavingsTokens > 0);
    }
}
