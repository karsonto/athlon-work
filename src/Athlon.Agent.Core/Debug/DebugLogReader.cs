using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Athlon.Agent.Core.Debug;

public sealed record DebugLogEntry(
    string RawLine,
    int LineNumber,
    DateTimeOffset? Timestamp,
    string? RunId,
    string? HypothesisId,
    string? Location,
    string? Message,
    string? DataJson);

public sealed record DebugLogReadResult(
    string Summary,
    string Body,
    IReadOnlyDictionary<string, int> HypothesisCounts);

public static class DebugLogReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static DebugLogReadResult Read(
        string path,
        string? hypothesisId = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        int limit = 200,
        int? tail = null)
    {
        if (!File.Exists(path))
        {
            return new DebugLogReadResult("Log file not found", $"No log file at `{path}`. Reproduce the bug after instrumentation.", new Dictionary<string, int>());
        }

        var lines = File.ReadAllLines(path);
        var entries = new List<DebugLogEntry>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            entries.Add(ParseLine(line, i + 1));
        }

        IEnumerable<DebugLogEntry> filtered = entries;
        if (!string.IsNullOrWhiteSpace(hypothesisId))
        {
            filtered = filtered.Where(e =>
                string.Equals(e.HypothesisId, hypothesisId, StringComparison.OrdinalIgnoreCase));
        }

        if (since is not null)
        {
            filtered = filtered.Where(e => e.Timestamp is null || e.Timestamp >= since);
        }

        if (until is not null)
        {
            filtered = filtered.Where(e => e.Timestamp is null || e.Timestamp <= until);
        }

        var list = filtered.ToList();
        if (tail is > 0 && list.Count > tail)
        {
            list = list.Skip(list.Count - tail.Value).ToList();
        }
        else if (list.Count > limit)
        {
            list = list.TakeLast(limit).ToList();
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
        {
            if (string.IsNullOrWhiteSpace(entry.HypothesisId))
            {
                continue;
            }

            counts.TryGetValue(entry.HypothesisId, out var count);
            counts[entry.HypothesisId] = count + 1;
        }

        var summary = list.Count == 0
            ? "No matching log entries"
            : $"Read {list.Count} log entries from `{path}`";
        if (counts.Count > 0)
        {
            summary += " | hypotheses: " + string.Join(", ", counts.Select(p => p.Key + "=" + p.Value));
        }

        var body = new StringBuilder();
        foreach (var entry in list)
        {
            body.Append('L').Append(entry.LineNumber).Append('|');
            if (entry.Timestamp is not null)
            {
                body.Append(entry.Timestamp.Value.ToString("O", CultureInfo.InvariantCulture)).Append(' ');
            }

            if (!string.IsNullOrWhiteSpace(entry.HypothesisId))
            {
                body.Append('[').Append(entry.HypothesisId).Append("] ");
            }

            if (!string.IsNullOrWhiteSpace(entry.Location))
            {
                body.Append('@').Append(entry.Location).Append(' ');
            }

            body.Append(entry.Message ?? entry.RawLine);
            if (!string.IsNullOrWhiteSpace(entry.DataJson))
            {
                body.Append(" data=").Append(entry.DataJson);
            }

            body.AppendLine();
        }

        return new DebugLogReadResult(summary, body.ToString().TrimEnd(), counts);
    }

    private static DebugLogEntry ParseLine(string line, int lineNumber)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            DateTimeOffset? ts = null;
            if (root.TryGetProperty("ts", out var tsNode)
                && DateTimeOffset.TryParse(tsNode.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
            {
                ts = parsed;
            }

            string? dataJson = null;
            if (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind != JsonValueKind.Null)
            {
                dataJson = dataNode.GetRawText();
            }

            return new DebugLogEntry(
                line,
                lineNumber,
                ts,
                root.TryGetProperty("runId", out var runId) ? runId.GetString() : null,
                root.TryGetProperty("hypothesisId", out var hypothesisId) ? hypothesisId.GetString() : null,
                root.TryGetProperty("location", out var location) ? location.GetString() : null,
                root.TryGetProperty("message", out var message) ? message.GetString() : null,
                dataJson);
        }
        catch (JsonException)
        {
            return new DebugLogEntry(line, lineNumber, null, null, null, null, line, null);
        }
    }
}
