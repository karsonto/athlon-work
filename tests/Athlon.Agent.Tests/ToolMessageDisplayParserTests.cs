using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ToolMessageDisplayParserTests
{
    [Fact]
    public void ParseToolContent_StripsMetadataAndPrettyPrintsJsonBody()
    {
        var content = string.Join(
            Environment.NewLine,
            "ToolCallId: call-42",
            "Tool `mcp_search` succeeded.",
            "",
            "Arguments: query=搜索",
            "Summary: Found 1 MCP tool(s) for query.",
            "",
            """{"query":"\u641c\u7d22","results":[{"description":"\u641c\u7d22\u5de5\u5177"}]}""");

        ToolMessageDisplayParser.ParseToolContent(
            content,
            out _,
            out _,
            out _,
            out _,
            out var detail,
            out _,
            out _);

        Assert.DoesNotContain("ToolCallId:", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments:", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Summary:", detail, StringComparison.Ordinal);
        Assert.Contains("```json", detail);
        Assert.Contains("搜索", detail);
        Assert.Contains("搜索工具", detail);
        Assert.DoesNotContain(@"\u641c", detail);
    }

    [Fact]
    public void FormatArgumentsFull_NonStringValues_UseReadableJson()
    {
        var arguments = ToolCallArgumentsParser.ParseJson("""{"filters":{"status":"进行中"},"topK":3}""");

        var formatted = ToolMessageDisplayParser.FormatArgumentsFull(arguments);

        Assert.Contains("进行中", formatted);
        Assert.DoesNotContain(@"\u8fdb", formatted);
    }
}
