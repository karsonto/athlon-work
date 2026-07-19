namespace Athlon.Agent.App.Services.Speech;

public static class ComposerSpeechText
{
    /// <summary>
    /// Appends recognized speech to the composer, inserting a single space when needed.
    /// </summary>
    public static string AppendTranscript(string existingComposerText, string transcript)
    {
        var spoken = transcript.Trim();
        if (spoken.Length == 0)
        {
            return existingComposerText;
        }

        if (string.IsNullOrEmpty(existingComposerText))
        {
            return spoken;
        }

        var needsSpace = !char.IsWhiteSpace(existingComposerText[^1]);
        return needsSpace
            ? existingComposerText + " " + spoken
            : existingComposerText + spoken;
    }
}
