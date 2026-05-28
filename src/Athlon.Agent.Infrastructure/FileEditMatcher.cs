using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Infrastructure;

internal readonly record struct OldTextCandidate(string Text, OldTextCandidateKind Kind);

internal enum OldTextCandidateKind
{
    Exact,
    StrippedLinePrefixes,
    CrlfNormalized,
    StrippedLinePrefixesCrlf
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
            var occurrences = CountOccurrences(content, candidate.Text);
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
            _ => originalNewText
        };

    public static string BuildNotFoundMessage(string oldText)
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
        return builder.ToString();
    }

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

    internal static string StripFileReadLinePrefixes(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
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
        return text.Contains("\r\n", StringComparison.Ordinal) ? joined.Replace("\n", "\r\n", StringComparison.Ordinal) : joined;
    }

    private static string ToCrlf(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? text : text.Replace("\n", "\r\n", StringComparison.Ordinal);

    private static int CountOccurrences(string content, string oldText) =>
        string.IsNullOrEmpty(oldText) ? 0 : content.Split(oldText, StringSplitOptions.None).Length - 1;

    private static string ReplaceFirst(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0 ? content : content[..index] + newText + content[(index + oldText.Length)..];
    }

    [GeneratedRegex(@"^\d+\|")]
    private static partial Regex FileReadLinePrefixRegex();
}
