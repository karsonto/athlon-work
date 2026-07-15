using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ToolCallArgumentsTests
{
    private const string ArgumentsJson =
        """{"text":"value","count":42,"enabled":true,"options":{"mode":"fast"},"items":[1,"two"],"nothing":null,"path":"src\\file.cs"}""";

    [Fact]
    public void ParseJson_PreservesEveryJsonValueKindAndOwnsValues()
    {
        var arguments = ToolCallArgumentsParser.ParseJson(ArgumentsJson);

        Assert.Equal("value", arguments.GetString("text"));
        Assert.Equal(42, arguments.GetInt32("count"));
        Assert.True(arguments.GetBoolean("enabled"));
        Assert.True(arguments.TryGetObject("options", out var options));
        Assert.Equal("fast", options.GetProperty("mode").GetString());
        Assert.True(arguments.TryGetArray("items", out var items));
        Assert.Equal(2, items.GetArrayLength());
        Assert.True(arguments.IsNull("nothing"));
    }

    [Fact]
    public void AssistantToolCallsCodec_RoundTripsNativeJsonTypes()
    {
        var encoded = AssistantToolCallsCodec.Serialize(
            [new AgentToolCall("call-1", "demo", ToolCallArgumentsParser.ParseJson(ArgumentsJson))]);
        var decoded = Assert.Single(AssistantToolCallsCodec.Deserialize(encoded)!);

        Assert.Equal(JsonValueKind.Number, decoded.Arguments["count"].ValueKind);
        Assert.Equal(JsonValueKind.True, decoded.Arguments["enabled"].ValueKind);
        Assert.Equal(JsonValueKind.Object, decoded.Arguments["options"].ValueKind);
        Assert.Equal(JsonValueKind.Array, decoded.Arguments["items"].ValueKind);
        Assert.Equal(JsonValueKind.Null, decoded.Arguments["nothing"].ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseNonStreamingResponse_PreservesNativeJsonTypes(bool argumentsAsString)
    {
        var functionArguments = argumentsAsString
            ? JsonSerializer.Serialize(ArgumentsJson)
            : ArgumentsJson;
        var response =
            "{\"choices\":[{\"message\":{\"content\":\"\",\"tool_calls\":[{\"id\":\"call-1\","
            + "\"function\":{\"name\":\"demo\",\"arguments\":"
            + functionArguments
            + "}}]}}]}";

        var call = Assert.Single(OpenAiChatResponseParser.ParseNonStreamingResponse(response).ToolCalls);

        Assert.Equal(JsonValueKind.String, call.Arguments["text"].ValueKind);
        Assert.Equal(JsonValueKind.Number, call.Arguments["count"].ValueKind);
        Assert.Equal(JsonValueKind.True, call.Arguments["enabled"].ValueKind);
        Assert.Equal(JsonValueKind.Object, call.Arguments["options"].ValueKind);
        Assert.Equal(JsonValueKind.Array, call.Arguments["items"].ValueKind);
        Assert.Equal(JsonValueKind.Null, call.Arguments["nothing"].ValueKind);
        Assert.Equal("src/file.cs", call.Arguments.GetString("path"));
    }

    [Fact]
    public void TryParse_SucceedsForEmptyAndValidObject()
    {
        Assert.True(ToolCallArguments.TryParse(null, out var empty, out var error));
        Assert.Empty(empty);
        Assert.Null(error);

        Assert.True(ToolCallArguments.TryParse("""{"path":"a.txt"}""", out var args, out error));
        Assert.Equal("a.txt", args.GetString("path"));
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_FailsForInvalidOrNonObjectJson()
    {
        Assert.False(ToolCallArguments.TryParse("{\"path\":\"a.txt\"", out var args, out var error));
        Assert.Empty(args);
        Assert.False(string.IsNullOrWhiteSpace(error));

        Assert.False(ToolCallArguments.TryParse("""["not","object"]""", out args, out error));
        Assert.Empty(args);
        Assert.Contains("object", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatToolResult_ShowsInvalidJsonWhenParseErrorPresent()
    {
        var call = new AgentToolCall("call-1", "file_write", ToolCallArguments.Empty)
        {
            RawArgumentsJson = """{"content":"hi","path":"a.html""",
            ArgumentsParseError = "Incomplete JSON."
        };

        var text = ModelMessageBuilder.FormatToolResult(
            call,
            ToolResult.Failure("Invalid tool arguments", "error"));

        Assert.Contains("(invalid JSON)", text, StringComparison.Ordinal);
        Assert.Contains("Incomplete JSON.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments: (none)", text, StringComparison.Ordinal);
    }
}
