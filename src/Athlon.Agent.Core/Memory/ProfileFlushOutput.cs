using System.Text;

namespace Athlon.Agent.Core.Memory;

/// <summary>Result of parsing a flush response that may carry one or more profile sections.</summary>
public sealed record ProfileFlushOutput(
    string? MemorySection,
    IReadOnlyDictionary<ProfileKind, string> ChangedDocuments);

/// <summary>
/// Parses the multi-section flush reply into the daily-ledger section plus any rewritten profile
/// documents. The parser is deliberately forgiving: an unparseable reply is treated as a plain
/// memory section so a malformed response can never blank out a profile document.
/// </summary>
public static class ProfileFlushOutputParser
{
    public const string UnchangedToken = "UNCHANGED";
    public const string MemoryHeading = "MEMORY";

    public static ProfileFlushOutput Parse(
        string? raw,
        IReadOnlyList<ProfileKind> expected,
        IReadOnlyDictionary<ProfileKind, string>? current)
    {
        var text = raw ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ProfileFlushOutput(null, new Dictionary<ProfileKind, string>());
        }

        var blocks = SplitSections(text);
        if (blocks.Count == 0)
        {
            // No recognizable headings: keep the legacy single-section behavior and touch no document.
            return new ProfileFlushOutput(text.Trim(), new Dictionary<ProfileKind, string>());
        }

        string? memory = null;
        var changed = new Dictionary<ProfileKind, string>();

        foreach (var (heading, body) in blocks)
        {
            if (string.Equals(heading, MemoryHeading, StringComparison.Ordinal))
            {
                memory = Clean(body);
                continue;
            }

            if (!TryMapHeading(heading, out var kind))
            {
                continue;
            }

            if (!expected.Contains(kind))
            {
                // Model volunteered a document we did not ask about: ignore rather than write blind.
                continue;
            }

            var content = Clean(body);
            if (content.Length == 0 || IsUnchanged(content))
            {
                continue;
            }

            if (current is not null
                && current.TryGetValue(kind, out var existing)
                && string.Equals(content.Trim(), existing.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            changed[kind] = content;
        }

        return new ProfileFlushOutput(memory, changed);
    }

    private static bool IsUnchanged(string content) =>
        string.Equals(content.Trim(), UnchangedToken, StringComparison.OrdinalIgnoreCase);

    private static string Clean(string body) => body.Trim();

    /// <summary>
    /// Collects blocks introduced by a recognized heading. A <c>## </c> line that is not a known
    /// section (for example a <c>## Preferences</c> sub-heading inside USER.md) is kept as body text,
    /// so a document's own structure never gets mistaken for the response contract.
    /// </summary>
    private static List<(string Heading, string Body)> SplitSections(string text)
    {
        var blocks = new List<(string, string)>();
        string? heading = null;
        var body = new StringBuilder();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var headingText = TryReadHeading(line);
            if (headingText is not null && IsKnownHeading(NormalizeHeading(headingText)))
            {
                if (heading is not null)
                {
                    blocks.Add((heading, body.ToString()));
                }

                heading = NormalizeHeading(headingText);
                body.Clear();
                continue;
            }

            if (heading is not null)
            {
                body.AppendLine(line);
            }
        }

        if (heading is not null)
        {
            blocks.Add((heading, body.ToString()));
        }

        return blocks;
    }

    private static bool IsKnownHeading(string normalized) =>
        string.Equals(normalized, MemoryHeading, StringComparison.Ordinal)
        || TryMapHeading(normalized, out _);

    private static string? TryReadHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("## ", StringComparison.Ordinal))
        {
            return null;
        }

        var heading = trimmed[3..].Trim();
        // Reject "###" style sub-headings produced inside a document body.
        if (heading.Length == 0 || heading.StartsWith('#'))
        {
            return null;
        }

        return heading;
    }

    private static string NormalizeHeading(string heading)
    {
        var normalized = heading.Trim();
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^3];
        }

        return CollapseWhitespace(normalized).ToUpperInvariant();
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    private static bool TryMapHeading(string normalized, out ProfileKind kind)
    {
        switch (normalized)
        {
            case "USER":
            case "USER PROFILE":
            case "USERPROFILE":
            case "HABITS":
                kind = ProfileKind.User;
                return true;
            case "LESSON":
            case "LESSONS":
            case "LESSON LEARNED":
            case "LESSONS LEARNED":
                kind = ProfileKind.Lessons;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
