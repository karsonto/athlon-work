using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class UnifiedDiffDisplayParserTests
{
    private const string SampleDiff = """
        --- a/src/App.tsx
        +++ b/src/App.tsx
        @@ -1,5 +1,6 @@
         line1
         line2
        -old
        +new
        +extra
         line4
         line5
        """;

    [Fact]
    public void CountChanges_counts_added_and_removed()
    {
        var counts = UnifiedDiffDisplayParser.CountChanges(SampleDiff);
        Assert.Equal(2, counts.Added);
        Assert.Equal(1, counts.Removed);
    }

    [Fact]
    public void Parse_maps_line_kinds()
    {
        var lines = UnifiedDiffDisplayParser.Parse(SampleDiff, foldContext: false);
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Header && line.Text.StartsWith("---", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Kind == DiffLineKind.HunkHeader);
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Removed && line.Text == "old");
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Added && line.Text == "new");
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Context && line.Text == "line1");
    }

    [Fact]
    public void Parse_folds_long_context_runs()
    {
        var diff = string.Join(
            "\n",
            "--- a/f.ts",
            "+++ b/f.ts",
            "@@ -1,6 +1,6 @@",
            " a",
            " b",
            " c",
            "-old",
            "+new",
            " d",
            " e",
            " f");

        var lines = UnifiedDiffDisplayParser.Parse(diff, foldContext: true);
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Collapsed && line.CollapsedCount == 3);
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Removed && line.Text == "old");
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Added && line.Text == "new");
    }
}
