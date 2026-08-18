using System.Text.RegularExpressions;

namespace Athlon.Agent.Core.Debug;

public static partial class DebugRunParser
{
    [GeneratedRegex(@"^\s*[-*]?\s*(H\d+)\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HypothesisLineRegex();

    [GeneratedRegex(@"##\s*Repro\s*steps\s*\n([\s\S]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ReproSectionRegex();

    public static IReadOnlyList<DebugHypothesis> ParseHypotheses(string? assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return [];
        }

        var results = new List<DebugHypothesis>();
        foreach (Match match in HypothesisLineRegex().Matches(assistantText))
        {
            var id = match.Groups[1].Value.ToUpperInvariant();
            var summary = match.Groups[2].Value.Trim();
            if (summary.Length == 0)
            {
                continue;
            }

            if (results.Any(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new DebugHypothesis(id, summary));
        }

        return results;
    }

    public static IReadOnlyList<DebugHypothesis> ParseHypothesesOrFallback(string? assistantText)
    {
        var parsed = ParseHypotheses(assistantText);
        if (parsed.Count > 0)
        {
            return parsed;
        }

        var summary = TruncateForHypothesis(assistantText);
        return
        [
            new DebugHypothesis("H1", string.IsNullOrWhiteSpace(summary) ? "Unparsed hypothesis from explore turn" : summary)
        ];
    }

    private static string TruncateForHypothesis(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var compact = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= 240 ? compact : compact[..237] + "...";
    }

    public static string? ParseReproSteps(string? assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return null;
        }

        var match = ReproSectionRegex().Match(assistantText);
        if (!match.Success)
        {
            return null;
        }

        var body = match.Groups[1].Value.Trim();
        return body.Length == 0 ? null : body;
    }

    public static string? GetLastAssistantText(AgentSession session)
    {
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            var message = session.Messages[i];
            if (message.Role == MessageRole.Assistant
                && !string.IsNullOrWhiteSpace(message.Content))
            {
                return message.Content;
            }
        }

        return null;
    }
}
