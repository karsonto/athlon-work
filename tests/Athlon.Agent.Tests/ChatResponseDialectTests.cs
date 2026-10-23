using System.Text.Json;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ChatResponseDialectTests
{
    [Fact]
    public void ParseNonStreaming_PeelsEmbeddedThinkingTags()
    {
        var content = "<redacted_thinking>step one</redacted_thinking>\nfinal answer";
        var response = $"{{\"choices\":[{{\"message\":{{\"content\":{JsonSerializer.Serialize(content)}}}]}}]}}";

        var parsed = OpenAiChatResponseParser.ParseNonStreamingResponse(response);

        Assert.Equal("final answer", parsed.Content);
        Assert.Equal("step one", parsed.ReasoningContent);
    }

    [Fact]
    public void ParseNonStreaming_PrefersDedicatedReasoningFieldOverTags()
    {
        var content = "<redacted_thinking>ignored</redacted_thinking>\nvisible";
        var response =
            "{\"choices\":[{\"message\":{"
            + $"\"content\":{JsonSerializer.Serialize(content)},"
            + "\"reasoning_content\":\"dedicated\""
            + "}}]}";

        var parsed = OpenAiChatResponseParser.ParseNonStreamingResponse(response);

        Assert.Equal(content, parsed.Content);
        Assert.Equal("dedicated", parsed.ReasoningContent);
    }

    [Fact]
    public void ParseNonStreaming_ReadsAlternateReasoningProperty()
    {
        var response =
            "{\"choices\":[{\"message\":{\"content\":\"ok\",\"reasoning\":\"alt\"}}]}";

        var parsed = OpenAiChatResponseParser.ParseNonStreamingResponse(response);

        Assert.Equal("ok", parsed.Content);
        Assert.Equal("alt", parsed.ReasoningContent);
    }
}
