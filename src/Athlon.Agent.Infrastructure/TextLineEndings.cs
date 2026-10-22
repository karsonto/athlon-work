using System.Text;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Line-ending helpers for file_edit / apply_patch. Treats a run of CR optionally
/// followed by LF (or a lone LF) as a single terminator so <c>\r\r\r\r\n</c> does
/// not expand into phantom blank lines.
/// </summary>
internal static class TextLineEndings
{
    /// <summary>
    /// Collapse <c>\r+\n?</c> and <c>\n</c> to a single <c>\n</c>.
    /// </summary>
    public static string NormalizeToLf(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\r') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                builder.Append('\n');
                continue;
            }

            if (c != '\r')
            {
                builder.Append(c);
                continue;
            }

            while (i + 1 < text.Length && text[i + 1] == '\r')
            {
                i++;
            }

            if (i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the text has multi-CR terminators or lone CR (not part of CRLF).
    /// </summary>
    public static bool IsNonstandard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r')
            {
                continue;
            }

            var j = i;
            while (j < text.Length && text[j] == '\r')
            {
                j++;
            }

            var crCount = j - i;
            var hasLf = j < text.Length && text[j] == '\n';
            if (crCount > 1 || (crCount == 1 && !hasLf))
            {
                return true;
            }

            i = j; // standard single \r\n; skip past the CR (LF handled next loop)
        }

        return false;
    }

    public static bool IsCrlf(string text) =>
        !string.IsNullOrEmpty(text)
        && text.Contains("\r\n", StringComparison.Ordinal)
        && !IsNonstandard(text);

    /// <summary>
    /// Map LF-patched text back to disk form. Nonstandard sources become clean LF;
    /// standard CRLF sources stay CRLF; otherwise LF.
    /// </summary>
    public static string ToDiskText(string original, string patchedLf)
    {
        if (IsNonstandard(original) || !IsCrlf(original))
        {
            return patchedLf;
        }

        return patchedLf.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    public static string NonstandardEolHint() =>
        " File uses nonstandard line endings (e.g. multiple CR before LF). "
        + "Edits normalize to LF when they succeed; on failure, re-read exact bytes or normalize the file first.";

    public static string WithNonstandardEolHint(string message, string? content) =>
        !string.IsNullOrEmpty(content) && IsNonstandard(content)
            ? message + NonstandardEolHint()
            : message;
}
