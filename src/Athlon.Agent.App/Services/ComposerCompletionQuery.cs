namespace Athlon.Agent.App.Services;

internal static class ComposerCompletionQuery
{
    public static bool TryGetAtQuerySpan(string text, int caretIndex, out int atStart, out int atEndExclusive)
    {
        atStart = -1;
        atEndExclusive = -1;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var safeCaret = Math.Clamp(caretIndex, 0, text.Length);
        var index = safeCaret - 1;
        while (index >= 0)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                break;
            }

            if (c == '@')
            {
                if (index > 0 && IsEmbeddedAtSign(text[index - 1]))
                {
                    index--;
                    continue;
                }

                var queryStart = index + 1;
                if (queryStart < safeCaret && ContainsMentionBreak(text, queryStart, safeCaret))
                {
                    return false;
                }

                atStart = index;
                atEndExclusive = safeCaret;
                return true;
            }

            index--;
        }

        return false;
    }

    private static bool ContainsMentionBreak(string text, int start, int endExclusive)
    {
        for (var i = start; i < endExclusive; i++)
        {
            if (text[i] is ' ' or '\t' or '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEmbeddedAtSign(char previous) =>
        char.IsLetterOrDigit(previous) || previous is '.' or '_' or '-';
}
