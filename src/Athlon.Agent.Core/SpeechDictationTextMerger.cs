namespace Athlon.Agent.Core;

public static class SpeechDictationTextMerger
{
    public static string Compose(string baseText, string committedText, string interimText) =>
        baseText + committedText + interimText;

    public static string AppendFinalSegment(string committedText, string finalSegment)
    {
        if (string.IsNullOrEmpty(finalSegment))
        {
            return committedText;
        }

        if (string.IsNullOrEmpty(committedText))
        {
            return finalSegment;
        }

        if (NeedsSeparator(committedText[^1], finalSegment[0]))
        {
            return committedText + " " + finalSegment;
        }

        return committedText + finalSegment;
    }

    private static bool NeedsSeparator(char previous, char next) =>
        char.IsAsciiLetterOrDigit(previous) && char.IsAsciiLetterOrDigit(next);
}
