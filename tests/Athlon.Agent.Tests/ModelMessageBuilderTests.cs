using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class ModelMessageBuilderTests
{
    [Fact]
    public void BuildForSession_AppendsTimestampToUserMessage()
    {
        var createdAt = new DateTimeOffset(2026, 6, 28, 6, 30, 0, TimeSpan.Zero);
        var history = new[]
        {
            new ChatMessage("user-1", MessageRole.User, "Hello", createdAt)
        };

        var cache = new ModelMessageCache();
        var result = ModelMessagesForApiBuilder.Build(cache, "system prompt", history, new ContextCompactionSettings());
        var userMessage = Assert.Single(result.Messages, message => message.Role == "user");
        var content = Assert.IsType<string>(userMessage.Content);

        Assert.StartsWith("Hello", content, StringComparison.Ordinal);
        Assert.EndsWith("[2026-06-28 14:30 UTC+8]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForSession_UserTimestamp_IsStableAcrossRebuilds()
    {
        var createdAt = new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            new ChatMessage("user-1", MessageRole.User, "Stable", createdAt)
        };

        var first = BuildUserContent(history);
        var second = BuildUserContent(history);

        Assert.Equal(first, second);
    }

    private static string BuildUserContent(IReadOnlyList<ChatMessage> history)
    {
        var cache = new ModelMessageCache();
        var result = ModelMessagesForApiBuilder.Build(cache, "system", history, new ContextCompactionSettings());
        var userMessage = result.Messages.Single(message => message.Role == "user");
        return Assert.IsType<string>(userMessage.Content);
    }
}
