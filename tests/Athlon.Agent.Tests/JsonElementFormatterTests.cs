using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class JsonElementFormatterTests
{
    [Fact]
    public void SerializeForDisplay_PreservesChineseCharacters()
    {
        var json = JsonElementFormatter.SerializeForDisplay(new { query = "搜索", label = "工具描述" });

        Assert.Contains("搜索", json);
        Assert.Contains("工具描述", json);
        Assert.DoesNotContain(@"\u641c", json);
        Assert.DoesNotContain(@"\u5de5", json);
    }

    [Fact]
    public void FormatForDisplay_ObjectElement_PreservesChineseCharacters()
    {
        using var document = JsonDocument.Parse("""{"title":"页面标题","count":2}""");
        var formatted = JsonElementFormatter.FormatForDisplay(document.RootElement);

        Assert.Contains("页面标题", formatted);
        Assert.DoesNotContain(@"\u9875", formatted);
    }

    [Fact]
    public void TryPrettyPrintJson_DecodesEscapedUnicodeLiterals()
    {
        const string escaped = """{"description":"\u641c\u7d22\u5de5\u5177"}""";

        var pretty = JsonElementFormatter.TryPrettyPrintJson(escaped);

        Assert.Contains("搜索工具", pretty);
        Assert.DoesNotContain(@"\u641c", pretty);
        Assert.Contains('\n', pretty);
    }

    [Fact]
    public void TryPrettyPrintJson_LeavesPlainTextUntouched()
    {
        const string text = "Found 3 matches\n./src/foo.cs:10";

        Assert.Equal(text, JsonElementFormatter.TryPrettyPrintJson(text));
    }

    [Fact]
    public void StripAndPrettyPrint_ToolResultBody_IsReadable()
    {
        var content = string.Join(
            Environment.NewLine,
            "ToolCallId: call-1",
            "Tool `mcp_search` succeeded.",
            "",
            "Arguments: query=搜索",
            "Summary: Found 1 MCP tool(s) for query.",
            "",
            """{"query":"\u641c\u7d22","results":[{"description":"\u641c\u7d22\u5de5\u5177"}]}""");

        var body = ModelMessageBuilder.StripToolCallIdAndMetadata(content);
        var pretty = JsonElementFormatter.TryPrettyPrintJson(body);

        Assert.Contains("搜索", pretty);
        Assert.Contains("搜索工具", pretty);
        Assert.DoesNotContain("ToolCallId:", pretty, StringComparison.Ordinal);
    }
}
