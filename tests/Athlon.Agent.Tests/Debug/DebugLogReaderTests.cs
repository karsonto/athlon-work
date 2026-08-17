using Athlon.Agent.Core.Debug;

namespace Athlon.Agent.Tests.Debug;

public sealed class DebugLogReaderTests
{
    [Fact]
    public void Read_ParsesJsonlAndFiltersByHypothesis()
    {
        var path = Path.Combine(Path.GetTempPath(), "athlon-debug-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                """{"ts":"2026-08-17T07:00:00.000Z","runId":"r1","hypothesisId":"H1","location":"A.cs:1","message":"enter","data":{"x":1}}""",
                """{"ts":"2026-08-17T07:00:01.000Z","runId":"r1","hypothesisId":"H2","location":"B.cs:2","message":"skip"}""",
                "not-json"
            ]);

            var result = DebugLogReader.Read(path, hypothesisId: "H1", limit: 50);
            Assert.Contains("H1", result.Summary, StringComparison.Ordinal);
            Assert.Equal(1, result.HypothesisCounts["H1"]);
            Assert.Contains("enter", result.Body, StringComparison.Ordinal);
            Assert.Contains("not-json", result.Body, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Read_ReturnsNotFoundWhenMissing()
    {
        var result = DebugLogReader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jsonl"));
        Assert.Contains("not found", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.HypothesisCounts);
    }
}
