using Athlon.Agent.App.Services.SlashCommands;

namespace Athlon.Agent.App.Services;

internal static class ComposerCompletionQuery
{
    public static bool TryGetActiveQuery(
        string text,
        int caretIndex,
        IComposerSlashCommandRegistry slashRegistry,
        out ComposerCompletionTrigger trigger,
        out int triggerStart,
        out int queryEndExclusive,
        out string query)
    {
        trigger = ComposerCompletionTrigger.None;
        triggerStart = -1;
        queryEndExclusive = -1;
        query = string.Empty;

        if (TryGetAtQuerySpan(text, caretIndex, out triggerStart, out queryEndExclusive))
        {
            trigger = ComposerCompletionTrigger.At;
            query = text[(triggerStart + 1)..queryEndExclusive];
            return true;
        }

        if (TryGetSlashQuerySpan(text, caretIndex, slashRegistry, out triggerStart, out queryEndExclusive, out var slashQuery))
        {
            trigger = ComposerCompletionTrigger.Slash;
            query = slashQuery.Query;
            return true;
        }

        return false;
    }

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

    public static bool TryGetSlashQuerySpan(
        string text,
        int caretIndex,
        IComposerSlashCommandRegistry slashRegistry,
        out int triggerStart,
        out int queryEndExclusive,
        out ComposerSlashQuery slashQuery)
    {
        triggerStart = -1;
        queryEndExclusive = -1;
        slashQuery = null!;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var safeCaret = Math.Clamp(caretIndex, 0, text.Length);
        if (!TryGetCurrentWordSpan(text, safeCaret, out var wordStart, out var wordEndExclusive))
        {
            return false;
        }

        if (wordEndExclusive - wordStart < 1 || text[wordStart] != '/')
        {
            return false;
        }

        if (wordStart + 1 < text.Length && text[wordStart + 1] == '/')
        {
            return false;
        }

        if (ContainsMentionBreak(text, wordStart + 1, safeCaret))
        {
            return false;
        }

        triggerStart = wordStart;
        queryEndExclusive = safeCaret;
        var query = text[(wordStart + 1)..safeCaret];
        var intent = IsExactSlashCommand(text, slashRegistry, query)
            ? ComposerSlashIntent.ExactCommand
            : ComposerSlashIntent.Discovery;
        slashQuery = new ComposerSlashQuery(triggerStart, queryEndExclusive, query, intent);
        return true;
    }

    private static bool IsExactSlashCommand(string text, IComposerSlashCommandRegistry slashRegistry, string query)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/')
            || (trimmed.Length > 1 && trimmed[1] == '/'))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(query) && !string.Equals(trimmed, $"/{query}", StringComparison.Ordinal))
        {
            return false;
        }

        return slashRegistry.TryGetExact(query, out _);
    }

    private static bool TryGetCurrentWordSpan(string text, int caretIndex, out int wordStart, out int wordEndExclusive)
    {
        wordStart = 0;
        wordEndExclusive = caretIndex;
        if (string.IsNullOrEmpty(text) || caretIndex <= 0)
        {
            return false;
        }

        var index = caretIndex - 1;
        while (index >= 0 && !char.IsWhiteSpace(text[index]))
        {
            index--;
        }

        wordStart = index + 1;
        return wordStart < caretIndex;
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
