using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class RequestHistoryHygieneTests
{
    [Fact]
    public void CompactTextForSummary_skips_heavy_compaction_for_evicted_placeholder()
    {
        var evicted = "[Tool result evicted to disk]\n"
            + string.Join('\n', Enumerable.Repeat("log line content", 5000));
        var result = RequestHistoryHygiene.CompactTextForSummary(evicted, new RequestHistoryHygieneSettings());

        Assert.DoesNotContain("[cache hygiene: omitted", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyToModelMessages_truncates_large_tool_payload()
    {
        var large = string.Join('\n', Enumerable.Range(1, 500).Select(index => $"line {index}"));
        var messages = new List<AgentModelMessage>
        {
            new("system", "sys"),
            new("tool", AgentRuntime.FormatToolResult(
                new AgentToolCall("call-1", "execute_command", new Dictionary<string, string>()),
                ToolResult.Success("ok", large)), "call-1")
        };

        var result = RequestHistoryHygiene.ApplyToModelMessages(messages, new RequestHistoryHygieneSettings());
        var text = Assert.IsType<string>(result.Messages[^1].Content);

        Assert.True(result.EstimatedSavingsTokens > 0);
        Assert.Contains("cache hygiene", text);
        Assert.DoesNotContain("line 250", text);
    }

    [Fact]
    public void ApplyToModelMessages_preserves_error_signal_lines()
    {
        var body = string.Join('\n',
            Enumerable.Range(1, 400).Select(index => $"info {index}")
                .Append("fatal: something failed"));
        var messages = new List<AgentModelMessage>
        {
            new("tool", body, "call-2")
        };

        var text = Assert.IsType<string>(
            RequestHistoryHygiene.ApplyToModelMessages(messages, new RequestHistoryHygieneSettings()).Messages[0].Content);

        Assert.Contains("fatal: something failed", text);
    }
}
