using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Infrastructure;

internal readonly record struct OldTextCandidate(string Text, OldTextCandidateKind Kind);

internal enum OldTextCandidateKind
{
    Exact,
    StrippedLinePrefixes,
    CrlfNormalized,
    StrippedLinePrefixesCrlf,
    LfNormalized,
    StrippedLfNormalized
}

internal enum FileEditMatchStatus
{
    Found,
    NotFound,
    NotUnique
}

internal readonly record struct FileEditMatchResult(
    FileEditMatchStatus Status,
    string MatchedOldText,
    string OriginalOldText,
    OldTextCandidateKind Kind,
    int Occurrences)
{
    public static FileEditMatchResult NotFound(string originalOldText) =>
        new(FileEditMatchStatus.NotFound, originalOldText, originalOldText, OldTextCandidateKind.Exact, 0);

    public static FileEditMatchResult NotUnique(string matchedOldText, OldTextCandidateKind kind, int occurrences) =>
        new(FileEditMatchStatus.NotUnique, matchedOldText, matchedOldText, kind, occurrences);

    public static FileEditMatchResult Found(string matchedOldText, OldTextCandidateKind kind, string originalOldText, int occurrences) =>
        new(FileEditMatchStatus.Found, matchedOldText, originalOldText, kind, occurrences);
}

internal static partial class FileEditMatcher
{
    private static readonly Regex FileReadLinePrefix = FileReadLinePrefixRegex();

    public static FileEditMatchResult TryMatch(string content, string oldText, bool replaceAll)
    {
        foreach (var candidate in EnumerateOldTextCandidates(content, oldText))
        {
            var haystack = HaystackFor(content, candidate.Kind);
            var occurrences = CountOccurrences(haystack, candidate.Text);
            if (occurrences == 0)
            {
                continue;
            }

            if (!replaceAll && occurrences != 1)
            {
                return FileEditMatchResult.NotUnique(candidate.Text, candidate.Kind, occurrences);
            }

            return FileEditMatchResult.Found(candidate.Text, candidate.Kind, oldText, occurrences);
        }

        return FileEditMatchResult.NotFound(oldText);
    }

    /// <summary>
    /// Apply a successful match and map the result to the correct on-disk line-ending style.
    /// </summary>
    public static string ApplyMatchedEdit(string content, FileEditMatchResult match, string originalNewText, bool replaceAll)
    {
        var effectiveNewText = ResolveNewText(match.OriginalOldText, originalNewText, match);

        if (match.Kind is OldTextCandidateKind.LfNormalized or OldTextCandidateKind.StrippedLfNormalized
            || TextLineEndings.IsNonstandard(content))
        {
            var lfContent = TextLineEndings.NormalizeToLf(content);
            var lfOld = TextLineEndings.NormalizeToLf(match.MatchedOldText);
            var lfNew = TextLineEndings.NormalizeToLf(effectiveNewText);
            var updatedLf = ApplyReplace(lfContent, lfOld, lfNew, replaceAll);
            return TextLineEndings.ToDiskText(content, updatedLf);
        }

        return ApplyReplace(content, match.MatchedOldText, effectiveNewText, replaceAll);
    }

    public static string ApplyReplace(string content, string matchedOldText, string newText, bool replaceAll) =>
        replaceAll
            ? content.Replace(matchedOldText, newText)
            : ReplaceFirst(content, matchedOldText, newText);

    public static string ResolveNewText(string originalOldText, string originalNewText, FileEditMatchResult match) =>
        match.Kind switch
        {
            OldTextCandidateKind.Exact => originalNewText,
            OldTextCandidateKind.StrippedLinePrefixes => StripFileReadLinePrefixes(originalNewText),
            OldTextCandidateKind.CrlfNormalized => ToCrlf(originalNewText),
            OldTextCandidateKind.StrippedLinePrefixesCrlf => ToCrlf(StripFileReadLinePrefixes(originalNewText)),
            OldTextCandidateKind.LfNormalized => TextLineEndings.NormalizeToLf(originalNewText),
            OldTextCandidateKind.StrippedLfNormalized =>
                TextLineEndings.NormalizeToLf(StripFileReadLinePrefixes(originalNewText)),
            _ => originalNewText
        };

    public static string BuildNotFoundMessage(string oldText) =>
        BuildNotFoundMessage(content: null, oldText);

    public static string BuildNotFoundMessage(string? content, string oldText)
    {
        var builder = new StringBuilder("old_text did not match the file on disk.");
        if (FileReadLinePrefix.IsMatch(oldText))
        {
            builder.Append(" Remove file_read line-number prefixes (N|) from old_text.");
        }
        else
        {
            builder.Append(" Copy exact text from the file (not from file_read's N|line format or grep path:line: output).");
        }

        builder.Append(" Check whitespace, indentation, and line endings (CRLF vs LF).");

        if (content is not null && TextLineEndings.IsNonstandard(content))
        {
            builder.Append(TextLineEndings.NonstandardEolHint());
        }

        var closestLine = TryFindClosestSimilarBlockLine(content, oldText);
        if (closestLine is > 0)
        {
            builder.Append(
                $" Closest similar block starts near line {closestLine}. Use file_read around that line, then retry or apply_patch.");
        }
        else
        {
            builder.Append(" Re-read the file and retry once, or use apply_patch with a unified diff.");
        }

        return builder.ToString();
    }

    public static string BuildNotUniqueMessage(string content, string matchedOldText, int occurrences)
    {
        var lineNumbers = CollectOccurrenceLineNumbers(content, matchedOldText, maxCount: 3);
        var builder = new StringBuilder(
            $"old_text matched {occurrences} times; it must match exactly once unless replace_all is true.");
        if (lineNumbers.Count > 0)
        {
            builder.Append(" Occurrences start near line(s) ");
            builder.Append(string.Join(", ", lineNumbers));
            builder.Append('.');
        }

        builder.Append(" Set replace_all=true, narrow old_text to a unique span, or use apply_patch.");
        return builder.ToString();
    }

    private static string HaystackFor(string content, OldTextCandidateKind kind) =>
        kind is OldTextCandidateKind.LfNormalized or OldTextCandidateKind.StrippedLfNormalized
            ? TextLineEndings.NormalizeToLf(content)
            : content;

    private static List<int> CollectOccurrenceLineNumbers(string content, string matchedOldText, int maxCount)
    {
        var lines = new List<int>(maxCount);
        if (string.IsNullOrEmpty(matchedOldText) || maxCount <= 0)
        {
            return lines;
        }

        var index = 0;
        while (lines.Count < maxCount
               && (index = content.IndexOf(matchedOldText, index, StringComparison.Ordinal)) >= 0)
        {
            lines.Add(GetOneBasedLineNumber(content, index));
            index += matchedOldText.Length;
        }

        return lines;
    }

    private static int GetOneBasedLineNumber(string content, int charIndex)
    {
        var line = 1;
        var limit = Math.Min(charIndex, content.Length);
        for (var i = 0; i < limit; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static int? TryFindClosestSimilarBlockLine(string? content, string oldText)
    {
        if (string.IsNullOrEmpty(content)
            || oldText.Length is 0 or > 800)
        {
            return null;
        }

        var needleLines = SplitLines(oldText);
        if (needleLines.Length is 0 or > 12)
        {
            return null;
        }

        var haystackLines = SplitLines(content);
        if (haystackLines.Length > 8000 || haystackLines.Length < needleLines.Length)
        {
            return null;
        }

        var anchor = needleLines.FirstOrDefault(static line => line.Length > 0);
        if (string.IsNullOrEmpty(anchor))
        {
            return null;
        }

        var bestLine = 0;
        var bestRatio = 1.0;
        for (var i = 0; i <= haystackLines.Length - needleLines.Length; i++)
        {
            if (!string.Equals(haystackLines[i], anchor, StringComparison.Ordinal))
            {
                continue;
            }

            var ratio = MismatchRatio(needleLines, haystackLines, i);
            if (ratio < bestRatio)
            {
                bestRatio = ratio;
                bestLine = i + 1;
            }
        }

        return bestRatio < 0.35 && bestLine > 0 ? bestLine : null;
    }

    private static double MismatchRatio(string[] needle, string[] haystack, int start)
    {
        var needleChars = 0;
        var mismatches = 0;
        for (var i = 0; i < needle.Length; i++)
        {
            var left = needle[i];
            var right = haystack[start + i];
            needleChars += Math.Max(left.Length, 1);
            var shared = Math.Min(left.Length, right.Length);
            mismatches += Math.Abs(left.Length - right.Length);
            for (var c = 0; c < shared; c++)
            {
                if (left[c] != right[c])
                {
                    mismatches++;
                }
            }
        }

        return needleChars == 0 ? 1.0 : (double)mismatches / needleChars;
    }

    private static string[] SplitLines(string text) =>
        TextLineEndings.NormalizeToLf(text).Split('\n');

    private static IEnumerable<OldTextCandidate> EnumerateOldTextCandidates(string content, string oldText)
    {
        yield return new OldTextCandidate(oldText, OldTextCandidateKind.Exact);

        var stripped = StripFileReadLinePrefixes(oldText);
        if (!string.Equals(stripped, oldText, StringComparison.Ordinal))
        {
            yield return new OldTextCandidate(stripped, OldTextCandidateKind.StrippedLinePrefixes);
        }

        if (!content.Contains('\r'))
        {
            yield break;
        }

        // Prefer standard CRLF candidates before LF-normalized matching so CRLF files keep CRLF.
        if (!TextLineEndings.IsNonstandard(content))
        {
            var crlf = ToCrlf(oldText);
            if (!string.Equals(crlf, oldText, StringComparison.Ordinal))
            {
                yield return new OldTextCandidate(crlf, OldTextCandidateKind.CrlfNormalized);
            }

            if (!string.Equals(stripped, oldText, StringComparison.Ordinal))
            {
                var strippedCrlf = ToCrlf(stripped);
                if (!string.Equals(strippedCrlf, stripped, StringComparison.Ordinal)
                    && !string.Equals(strippedCrlf, crlf, StringComparison.Ordinal))
                {
                    yield return new OldTextCandidate(strippedCrlf, OldTextCandidateKind.StrippedLinePrefixesCrlf);
                }
            }
        }

        var lfOld = TextLineEndings.NormalizeToLf(oldText);
        if (!string.Equals(lfOld, oldText, StringComparison.Ordinal)
            || TextLineEndings.IsNonstandard(content)
            || !string.Equals(TextLineEndings.NormalizeToLf(content), content, StringComparison.Ordinal))
        {
            yield return new OldTextCandidate(lfOld, OldTextCandidateKind.LfNormalized);

            var lfStripped = TextLineEndings.NormalizeToLf(stripped);
            if (!string.Equals(lfStripped, lfOld, StringComparison.Ordinal))
            {
                yield return new OldTextCandidate(lfStripped, OldTextCandidateKind.StrippedLfNormalized);
            }
        }
    }

    internal static string StripFileReadLinePrefixes(string text)
    {
        var normalized = TextLineEndings.NormalizeToLf(text);
        var lines = normalized.Split('\n');
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!FileReadLinePrefix.IsMatch(line))
            {
                continue;
            }

            lines[index] = FileReadLinePrefix.Replace(line, string.Empty);
            changed = true;
        }

        if (!changed)
        {
            return text;
        }

        var joined = string.Join('\n', lines);
        return TextLineEndings.IsCrlf(text) ? joined.Replace("\n", "\r\n", StringComparison.Ordinal) : joined;
    }

    private static string ToCrlf(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? text : text.Replace("\n", "\r\n", StringComparison.Ordinal);

    private static int CountOccurrences(string content, string oldText)
    {
        if (string.IsNullOrEmpty(oldText))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(oldText, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += oldText.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0 ? content : content[..index] + newText + content[(index + oldText.Length)..];
    }

    [GeneratedRegex(@"^\d+\|")]
    private static partial Regex FileReadLinePrefixRegex();
}
