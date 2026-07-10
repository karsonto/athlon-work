using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class PromptCachePrefixTests
{
    [Fact]
    public void Build_keeps_system_and_history_prefix_stable_when_runtime_context_changes()
    {
        var cache = new ModelMessageCache();
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "question")
        };

        var first = ModelMessagesForApiBuilder.Build(
            cache,
            "frozen system",
            history,
            new ContextCompactionSettings(),
            "runtime one");

        history.Add(ChatMessage.Create(MessageRole.Assistant, "working"));
        var second = ModelMessagesForApiBuilder.Build(
            cache,
            "frozen system",
            history,
            new ContextCompactionSettings(),
            "runtime two");

        Assert.Equal(first.Messages[0], second.Messages[0]);
        Assert.Equal(first.Messages[1], second.Messages[1]);
        Assert.Equal("runtime one", first.Messages[^1].Content);
        Assert.Equal("runtime two", second.Messages[^1].Content);
        Assert.Equal("working", second.Messages[^2].Content);
    }
}
