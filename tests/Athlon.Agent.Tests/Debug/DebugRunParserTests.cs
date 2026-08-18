using Athlon.Agent.Core;
using Athlon.Agent.Core.Debug;

namespace Athlon.Agent.Tests.Debug;

public sealed class DebugRunParserTests
{
    [Fact]
    public void ParseHypotheses_FindsNumberedLines()
    {
        const string text = """
            Here are hypotheses:
            H1: Null reference when cache misses
            - H2: Race in async loader
            """;

        var hypotheses = DebugRunParser.ParseHypotheses(text);
        Assert.Equal(2, hypotheses.Count);
        Assert.Equal("H1", hypotheses[0].Id);
        Assert.Contains("Null reference", hypotheses[0].Summary, StringComparison.Ordinal);
        Assert.Equal("H2", hypotheses[1].Id);
    }

    [Fact]
    public void ParseReproSteps_FindsMarkdownSection()
    {
        const string text = """
            Done instrumenting.

            ## Repro steps
            1. Open settings
            2. Click save twice
            """;

        var steps = DebugRunParser.ParseReproSteps(text);
        Assert.NotNull(steps);
        Assert.Contains("Click save twice", steps, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseHypothesesOrFallback_UsesH1WhenUnformatted()
    {
        var hypotheses = DebugRunParser.ParseHypothesesOrFallback("The loader races on cache miss.");
        Assert.Single(hypotheses);
        Assert.Equal("H1", hypotheses[0].Id);
        Assert.Contains("loader races", hypotheses[0].Summary, StringComparison.Ordinal);
    }
}
