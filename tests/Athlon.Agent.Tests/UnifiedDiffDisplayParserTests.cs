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

    [Fact]
    public void BuildReview_keeps_three_lines_of_context_beside_a_change()
    {
        var diff = string.Join(
            "\n",
            "--- a/f.ts",
            "+++ b/f.ts",
            "@@ -1,8 +1,8 @@",
            " a",
            " b",
            " c",
            "-old",
            "+new",
            " d",
            " e",
            " f");

        var review = UnifiedDiffDisplayParser.BuildReview(diff);

        Assert.DoesNotContain(review.Lines, line => line.Kind is DiffLineKind.Header or DiffLineKind.HunkHeader);
        Assert.DoesNotContain(review.Lines, line => line.Text.Contains("@@", StringComparison.Ordinal));
        Assert.Equal(
            ["a", "b", "c", "old", "new", "d", "e", "f"],
            review.Lines.Select(line => line.Text));
        Assert.Equal(DiffLineKind.Removed, review.Lines[3].Kind);
        Assert.Equal(4, review.Lines[3].LineNumber);
        Assert.Equal(DiffLineKind.Added, review.Lines[4].Kind);
        Assert.Equal(4, review.Lines[4].LineNumber);
    }

    [Fact]
    public void BuildReview_folds_the_middle_of_a_long_context_run_and_keeps_the_text()
    {
        var context = Enumerable.Range(1, 10).Select(i => " c" + i);
        var diff = string.Join(
            "\n",
            new[]
            {
                "--- a/f.ts",
                "+++ b/f.ts",
                "@@ -1,14 +1,14 @@",
                "-old1",
                "+new1"
            }.Concat(context).Concat(
            [
                "-old2",
                "+new2"
            ]));

        var review = UnifiedDiffDisplayParser.BuildReview(diff);
        var collapsed = Assert.Single(review.Lines, line => line.Kind == DiffLineKind.Collapsed);

        Assert.True(collapsed.CanExpand);
        Assert.Equal(4, collapsed.CollapsedCount);
        Assert.Equal(["c4", "c5", "c6", "c7"], collapsed.HiddenLines!.Select(line => line.Text));
        Assert.Equal(
            ["old1", "new1", "c1", "c2", "c3", "c8", "c9", "c10", "old2", "new2"],
            review.Lines.Where(line => line.Kind != DiffLineKind.Collapsed).Select(line => line.Text));
    }

    [Fact]
    public void BuildReview_reports_unmodified_lines_before_the_first_hunk_and_between_hunks()
    {
        var diff = string.Join(
            "\n",
            "--- a/f.ts",
            "+++ b/f.ts",
            "@@ -164,6 +164,6 @@",
            " a",
            " b",
            " c",
            "-old",
            "+new",
            " d",
            "@@ -300,1 +300,1 @@",
            "-x",
            "+y");

        var review = UnifiedDiffDisplayParser.BuildReview(diff);
        var gaps = review.Lines.Where(line => line.Kind == DiffLineKind.Collapsed).ToArray();

        Assert.Equal(2, gaps.Length);
        Assert.Equal(163, gaps[0].CollapsedCount);
        Assert.False(gaps[0].CanExpand);
        Assert.Null(gaps[0].HiddenLines);
        Assert.Equal(164, review.Lines.First(line => line.Kind == DiffLineKind.Context).LineNumber);
        Assert.Equal(167, review.Lines.First(line => line.Kind == DiffLineKind.Removed).LineNumber);
        Assert.Equal(167, review.Lines.First(line => line.Kind == DiffLineKind.Added).LineNumber);

        Assert.Equal(131, gaps[1].CollapsedCount);
        Assert.False(gaps[1].CanExpand);
        Assert.Equal(300, review.LastNewLine);
        Assert.DoesNotContain(review.Lines, line => line.Kind is DiffLineKind.Header or DiffLineKind.HunkHeader);
        Assert.DoesNotContain(review.Lines, line => line.Text.Contains("@@", StringComparison.Ordinal));
    }
}
