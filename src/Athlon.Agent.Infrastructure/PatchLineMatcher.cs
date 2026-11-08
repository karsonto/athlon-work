namespace Athlon.Agent.Infrastructure;

internal static class PatchLineMatcher
{
    internal enum LineMatch
    {
        Exact,
        TrimEnd,
        Trim,
        Normalized
    }

    internal static int? Seek(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> pattern,
        int start,
        bool eof,
        out LineMatch kind)
    {
        kind = LineMatch.Exact;
        if (pattern.Count == 0)
        {
            return Math.Clamp(start, 0, lines.Count);
        }

        if (pattern.Count > lines.Count)
        {
            return null;
        }

        var last = lines.Count - pattern.Count;
        var searchStart = eof ? last : start;
        if (searchStart < 0 || searchStart > last)
        {
            return null;
        }

        if (TryPass(lines, pattern, searchStart, last, LineEqualsExact))
        {
            kind = LineMatch.Exact;
            return Find(lines, pattern, searchStart, last, LineEqualsExact);
        }

        if (TryPass(lines, pattern, searchStart, last, LineEqualsTrimEnd))
        {
            kind = LineMatch.TrimEnd;
            return Find(lines, pattern, searchStart, last, LineEqualsTrimEnd);
        }

        if (TryPass(lines, pattern, searchStart, last, LineEqualsTrim))
        {
            kind = LineMatch.Trim;
            return Find(lines, pattern, searchStart, last, LineEqualsTrim);
        }

        if (TryPass(lines, pattern, searchStart, last, LineEqualsNormalized))
        {
            kind = LineMatch.Normalized;
            return Find(lines, pattern, searchStart, last, LineEqualsNormalized);
        }

        return null;
    }

    internal static bool TryPass(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> pattern,
        int searchStart,
        int last,
        Func<string, string, bool> equals) =>
        Find(lines, pattern, searchStart, last, equals) is not null;

    internal static int? Find(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> pattern,
        int searchStart,
        int last,
        Func<string, string, bool> equals)
    {
        for (var i = searchStart; i <= last; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Count; j++)
            {
                if (!equals(lines[i + j], pattern[j]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return null;
    }

    internal static bool LineEqualsExact(string fileLine, string pattern) =>
        string.Equals(fileLine, pattern, StringComparison.Ordinal);

    internal static bool LineEqualsTrimEnd(string fileLine, string pattern) =>
        string.Equals(fileLine.TrimEnd(), pattern.TrimEnd(), StringComparison.Ordinal);

    internal static bool LineEqualsTrim(string fileLine, string pattern) =>
        string.Equals(fileLine.Trim(), pattern.Trim(), StringComparison.Ordinal);

    internal static bool LineEqualsNormalized(string fileLine, string pattern) =>
        string.Equals(Normalize(fileLine), Normalize(pattern), StringComparison.Ordinal);

    internal static string Normalize(string text)
    {
        var trimmed = text.Trim();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            builder.Append(c switch
            {
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
                '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
                '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
                '\u00A0' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006'
                    or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u202F' or '\u205F' or '\u3000' => ' ',
                _ => c
            });
        }

        return builder.ToString();
    }
}
