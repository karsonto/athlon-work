using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class FileWriteToolArgumentsDisplayTests
{
    [Fact]
    public void FormatStreaming_shows_path_and_streaming_label()
    {
        var text = FileWriteToolArgumentsDisplay.FormatStreaming("src/App.tsx");

        Assert.Contains("path = src/App.tsx", text, StringComparison.Ordinal);
        Assert.Contains(FileWriteToolArgumentsDisplay.StreamingContentLabel, text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatFinal_shows_char_count()
    {
        var text = FileWriteToolArgumentsDisplay.FormatFinal("src/App.tsx", 12345);

        Assert.Contains("path = src/App.tsx", text, StringComparison.Ordinal);
        Assert.Contains("content = (12345 chars)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatFinalFromRawJson_uses_complete_parse_when_available()
    {
        const string json = """{"path":"src/App.tsx","content":"hello\nworld"}""";

        var text = FileWriteToolArgumentsDisplay.FormatFinalFromRawJson(json);

        Assert.Contains("path = src/App.tsx", text, StringComparison.Ordinal);
        Assert.Contains("content = (11 chars)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatFinalFromRawJson_estimates_length_from_partial_json()
    {
        const string partial = """{"path":"a.ts","content":"abc""";

        var text = FileWriteToolArgumentsDisplay.FormatFinalFromRawJson(partial);

        Assert.Contains("path = a.ts", text, StringComparison.Ordinal);
        Assert.Contains("content = (3 chars)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatArgumentsForPersistedDisplay_hides_content_body()
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ToolPathNormalizer.PathArgumentName] = "src/App.tsx",
            ["content"] = new string('x', 500)
        };

        var text = FileWriteToolArgumentsDisplay.FormatArgumentsForPersistedDisplay(arguments);

        Assert.Contains("path = src/App.tsx", text, StringComparison.Ordinal);
        Assert.Contains("content = (500 chars)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xxx", text, StringComparison.Ordinal);
    }
}
