using System.Text;

namespace Athlon.Agent.Core.Compaction;

public static class PrefixToolResultReEvictor
{
    public static (IReadOnlyList<ChatMessage> Messages, bool Changed) Apply(
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings settings,
        int prefixCutoffExclusive)
    {
        if (prefixCutoffExclusive <= 0)
        {
            return (messages, false);
        }

        var previewChars = Math.Max(256, settings.ToolResultEviction.PreviewChars / 2);
        var changed = false;
        var updated = new List<ChatMessage>(messages.Count);

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (index >= prefixCutoffExclusive
                || message.Role != MessageRole.Tool
                || !IsEvictedPlaceholder(message.Content))
            {
                updated.Add(message);
                continue;
            }

            var tightened = TightenPreview(message.Content!, previewChars);
            if (!string.Equals(tightened, message.Content, StringComparison.Ordinal))
            {
                message = message with { Content = tightened };
                changed = true;
            }

            updated.Add(message);
        }

        return changed ? (updated, true) : (messages, false);
    }

    private static bool IsEvictedPlaceholder(string? content) =>
        !string.IsNullOrWhiteSpace(content)
        && content.Contains("[Tool result evicted", StringComparison.OrdinalIgnoreCase);

    private static string TightenPreview(string content, int previewChars)
    {
        var marker = "Preview:";
        var markerIndex = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return content.Length <= previewChars * 3
                ? content
                : content[..Math.Min(content.Length, previewChars * 3)] + "\n...(preview tightened)";
        }

        var head = content[..(markerIndex + marker.Length)];
        var preview = content[(markerIndex + marker.Length)..].TrimStart();
        if (preview.Length <= previewChars * 2)
        {
            return content;
        }

        var shortened = preview[..previewChars] + "\n...\n" + preview[^previewChars..];
        return head + Environment.NewLine + shortened;
    }
}
