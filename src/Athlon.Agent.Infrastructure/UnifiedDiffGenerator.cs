using System.Text;

namespace Athlon.Agent.Infrastructure;

/// <summary>Generates a unified-diff patch from two versions of a file.</summary>
internal static class UnifiedDiffGenerator
{
    /// <summary>Generate a complete unified-diff between original and updated content.</summary>
    public static string Generate(string originalContent, string updatedContent, string relativePath, int contextLines = 3)
    {
        var originalLines = SplitLines(originalContent);
        var updatedLines = SplitLines(updatedContent);

        // Find first differing line index
        var firstDiff = 0;
        while (firstDiff < originalLines.Length
            && firstDiff < updatedLines.Length
            && string.Equals(originalLines[firstDiff], updatedLines[firstDiff], StringComparison.Ordinal))
        {
            firstDiff++;
        }

        // No changes at all
        if (firstDiff == originalLines.Length && firstDiff == updatedLines.Length)
            return string.Empty;

        // Find last differing line index
        var lastOriginal = originalLines.Length - 1;
        var lastUpdated = updatedLines.Length - 1;
        while (lastOriginal >= firstDiff
            && lastUpdated >= firstDiff
            && string.Equals(originalLines[lastOriginal], updatedLines[lastUpdated], StringComparison.Ordinal))
        {
            lastOriginal--;
            lastUpdated--;
        }

        // Context window boundaries
        var contextBefore = Math.Max(0, firstDiff - contextLines);
        var originalEnd = Math.Min(originalLines.Length - 1, lastOriginal + contextLines);
        var updatedEnd = Math.Min(updatedLines.Length - 1, lastUpdated + contextLines);

        var sb = new StringBuilder();
        sb.Append("--- a/");
        sb.AppendLine(relativePath);
        sb.Append("+++ b/");
        sb.AppendLine(relativePath);

        var oldStart = contextBefore + 1;
        var oldCount = Math.Max(1, originalEnd - contextBefore + 1);
        var newStart = contextBefore + 1;
        var newCount = Math.Max(1, updatedEnd - contextBefore + 1);

        sb.Append("@@ -");
        sb.Append(oldStart);
        if (oldCount != 1) { sb.Append(','); sb.Append(oldCount); }
        sb.Append(" +");
        sb.Append(newStart);
        if (newCount != 1) { sb.Append(','); sb.Append(newCount); }
        sb.AppendLine(" @@");

        // Context before the change
        for (var i = contextBefore; i < firstDiff; i++)
        {
            sb.Append(' ');
            sb.AppendLine(originalLines[i]);
        }

        // Removed lines
        for (var i = firstDiff; i <= lastOriginal; i++)
        {
            sb.Append('-');
            sb.AppendLine(originalLines[i]);
        }

        // Added lines
        for (var i = firstDiff; i <= lastUpdated; i++)
        {
            sb.Append('+');
            sb.AppendLine(updatedLines[i]);
        }

        // Context after the change
        for (var i = lastUpdated + 1; i <= updatedEnd; i++)
        {
            sb.Append(' ');
            sb.AppendLine(updatedLines[i]);
        }

        return sb.ToString();
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }
}
