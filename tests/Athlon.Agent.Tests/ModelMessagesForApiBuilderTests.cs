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
        var content = Assert.IsType<string>(toolMessage.Content);
        Assert.Contains("[cache hygiene:", content, StringComparison.Ordinal);
        Assert.True(result.EstimatedSavingsTokens > 0);
    }

    [Fact]
    public void Build_appends_runtime_context_without_mutating_cached_prefix()
    {
        var cache = new ModelMessageCache();
        var history = new[] { ChatMessage.Create(MessageRole.User, "question") };

        var first = ModelMessagesForApiBuilder.Build(
            cache,
            "stable system",
            history,
            new ContextCompactionSettings(),
            "runtime one");
        var second = ModelMessagesForApiBuilder.Build(
            cache,
            "stable system",
            history,
            new ContextCompactionSettings(),
            "runtime two");

        Assert.Equal(first.Messages.Count, second.Messages.Count);
        Assert.Equal("stable system", first.Messages[0].Content);
        Assert.Equal(first.Messages[0], second.Messages[0]);
        Assert.Equal("runtime one", first.Messages[^1].Content);
        Assert.Equal("runtime two", second.Messages[^1].Content);
        Assert.Single(first.Messages, message => Equals(message.Content, "runtime one"));
        Assert.Single(second.Messages, message => Equals(message.Content, "runtime two"));
    }
}
