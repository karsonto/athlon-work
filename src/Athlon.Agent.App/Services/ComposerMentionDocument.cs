using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Athlon.Agent.App.Controls;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services;

public enum ComposerMentionKind
{
    File,
    Skill,
    Mcp
}

public readonly record struct ComposerMentionSpan(
    int Start,
    int Length,
    string InsertText,
    string DisplayName,
    string RelativePath,
    ComposerMentionKind Kind,
    WorkspaceFileIconKind IconKind);

/// <summary>
/// Serializes a composer <see cref="FlowDocument"/> to mention tokens and hydrates chips back.
/// </summary>
public static class ComposerMentionDocument
{
    private const string SkillPrefix = "//skill:";
    private const string McpPrefix = "//mcp:";

    public static string Serialize(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        foreach (var inline in EnumerateInlines(document))
        {
            AppendInline(builder, inline);
        }

        return builder.ToString();
    }

    public static void Hydrate(
        FlowDocument document,
        string composerText,
        int excludeStart = -1,
        int excludeEnd = -1)
    {
        ArgumentNullException.ThrowIfNull(document);
        var paragraph = EnsureSingleParagraph(document);
        paragraph.Inlines.Clear();

        if (string.IsNullOrEmpty(composerText))
        {
            return;
        }

        var mentions = ParseMentions(composerText, excludeStart, excludeEnd);
        var mentionIndex = 0;
        var i = 0;
        while (i < composerText.Length)
        {
            if (mentionIndex < mentions.Count && mentions[mentionIndex].Start == i)
            {
                var mention = mentions[mentionIndex];
                paragraph.Inlines.Add(CreateChipContainer(mention));
                i += mention.Length;
                mentionIndex++;
                continue;
            }

            var nextMention = mentionIndex < mentions.Count ? mentions[mentionIndex].Start : composerText.Length;
            var chunk = composerText[i..nextMention];
            AppendPlainText(paragraph, chunk);
            i = nextMention;
        }
    }

    public static IReadOnlyList<ComposerMentionSpan> ParseMentions(
        string composerText,
        int excludeStart = -1,
        int excludeEnd = -1)
    {
        if (string.IsNullOrEmpty(composerText))
        {
            return Array.Empty<ComposerMentionSpan>();
        }

        var spans = new List<ComposerMentionSpan>();
        var i = 0;
        while (i < composerText.Length)
        {
            if (TryReadFileMention(composerText, i, excludeStart, excludeEnd, out var fileMention))
            {
                spans.Add(fileMention);
                i += fileMention.Length;
                continue;
            }

            if (TryReadPrefixedMention(
                    composerText,
                    i,
                    SkillPrefix,
                    ComposerMentionKind.Skill,
                    excludeStart,
                    excludeEnd,
                    out var skillMention))
            {
                spans.Add(skillMention);
                i += skillMention.Length;
                continue;
            }

            if (TryReadPrefixedMention(
                    composerText,
                    i,
                    McpPrefix,
                    ComposerMentionKind.Mcp,
                    excludeStart,
                    excludeEnd,
                    out var mcpMention))
            {
                spans.Add(mcpMention);
                i += mcpMention.Length;
                continue;
            }

            i++;
        }

        return spans;
    }

    private static bool TryReadFileMention(
        string composerText,
        int index,
        int excludeStart,
        int excludeEnd,
        out ComposerMentionSpan mention)
    {
        mention = default;
        if (composerText[index] != '@'
            || (index > 0 && IsEmbeddedAtSign(composerText[index - 1])))
        {
            return false;
        }

        var pathStart = index + 1;
        var pathEnd = pathStart;
        while (pathEnd < composerText.Length && !char.IsWhiteSpace(composerText[pathEnd]))
        {
            pathEnd++;
        }

        if (pathEnd == pathStart)
        {
            return false;
        }

        var relative = composerText[pathStart..pathEnd].Replace('\\', '/');
        var length = IncludeTrailingSpace(composerText, index, pathEnd);
        if (IsExcluded(index, length, excludeStart, excludeEnd))
        {
            return false;
        }

        var isFolder = relative.EndsWith('/');
        var displayPath = relative.TrimEnd('/');
        var displayName = Path.GetFileName(displayPath);
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = displayPath;
        }

        mention = new ComposerMentionSpan(
            Start: index,
            Length: length,
            InsertText: composerText.AsSpan(index, length).ToString(),
            DisplayName: displayName,
            RelativePath: relative,
            Kind: ComposerMentionKind.File,
            IconKind: WorkspaceFileIconResolver.Resolve(
                displayName,
                relative,
                isDirectory: isFolder,
                isPlaceholder: false));
        return true;
    }

    private static bool TryReadPrefixedMention(
        string composerText,
        int index,
        string prefix,
        ComposerMentionKind kind,
        int excludeStart,
        int excludeEnd,
        out ComposerMentionSpan mention)
    {
        mention = default;
        if (!IsWordStart(composerText, index)
            || index + prefix.Length >= composerText.Length
            || !composerText.AsSpan(index).StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var idStart = index + prefix.Length;
        if (idStart >= composerText.Length || char.IsWhiteSpace(composerText[idStart]))
        {
            return false;
        }

        var idEnd = idStart;
        while (idEnd < composerText.Length && !char.IsWhiteSpace(composerText[idEnd]))
        {
            idEnd++;
        }

        var id = composerText[idStart..idEnd];
        var length = IncludeTrailingSpace(composerText, index, idEnd);
        if (IsExcluded(index, length, excludeStart, excludeEnd))
        {
            return false;
        }

        mention = new ComposerMentionSpan(
            Start: index,
            Length: length,
            InsertText: composerText.AsSpan(index, length).ToString(),
            DisplayName: id,
            RelativePath: id,
            Kind: kind,
            IconKind: WorkspaceFileIconKind.File);
        return true;
    }

    private static int IncludeTrailingSpace(string composerText, int start, int tokenEnd)
    {
        var length = tokenEnd - start;
        if (tokenEnd < composerText.Length && composerText[tokenEnd] == ' ')
        {
            length++;
        }

        return length;
    }

    private static bool IsExcluded(int start, int length, int excludeStart, int excludeEnd) =>
        excludeStart >= 0
        && excludeEnd > excludeStart
        && start < excludeEnd
        && start + length > excludeStart;

    private static bool IsWordStart(string composerText, int index) =>
        index == 0 || char.IsWhiteSpace(composerText[index - 1]);

    public static int GetSerializedOffset(FlowDocument document, TextPointer caret)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(caret);
        var offset = 0;
        foreach (var inline in EnumerateInlines(document))
        {
            if (inline is InlineUIContainer container && container.Child is ComposerFileChip chip)
            {
                var insert = chip.InsertText ?? string.Empty;
                if (caret.CompareTo(container.ElementStart) <= 0)
                {
                    return offset;
                }

                if (caret.CompareTo(container.ElementEnd) <= 0)
                {
                    return offset + insert.Length;
                }

                offset += insert.Length;
                continue;
            }

            if (inline is LineBreak lineBreak)
            {
                if (caret.CompareTo(lineBreak.ElementStart) <= 0)
                {
                    return offset;
                }

                if (caret.CompareTo(lineBreak.ElementEnd) <= 0)
                {
                    return offset + 1;
                }

                offset += 1;
                continue;
            }

            if (inline is Run run)
            {
                var text = run.Text ?? string.Empty;
                if (caret.CompareTo(run.ContentStart) <= 0)
                {
                    return offset;
                }

                if (caret.CompareTo(run.ContentEnd) <= 0)
                {
                    var local = new TextRange(run.ContentStart, caret).Text ?? string.Empty;
                    return offset + local.Replace("\r\n", "\n").Replace('\r', '\n').Length;
                }

                offset += text.Replace("\r\n", "\n").Replace('\r', '\n').Length;
            }
        }

        return offset;
    }

    public static TextPointer GetPointerAtOffset(FlowDocument document, int serializedOffset)
    {
        ArgumentNullException.ThrowIfNull(document);
        var remaining = Math.Max(0, serializedOffset);
        TextPointer? last = document.ContentEnd;
        foreach (var inline in EnumerateInlines(document))
        {
            last = inline.ElementEnd;
            if (inline is InlineUIContainer container && container.Child is ComposerFileChip chip)
            {
                var insert = chip.InsertText ?? string.Empty;
                if (remaining <= 0)
                {
                    return container.ElementStart;
                }

                if (remaining < insert.Length)
                {
                    return remaining <= insert.Length / 2 ? container.ElementStart : container.ElementEnd;
                }

                remaining -= insert.Length;
                continue;
            }

            if (inline is LineBreak lineBreak)
            {
                if (remaining <= 0)
                {
                    return lineBreak.ElementStart;
                }

                remaining -= 1;
                continue;
            }

            if (inline is Run run)
            {
                var text = run.Text ?? string.Empty;
                var length = text.Replace("\r\n", "\n").Replace('\r', '\n').Length;
                if (remaining <= 0)
                {
                    return run.ContentStart;
                }

                if (remaining <= length)
                {
                    return run.ContentStart.GetPositionAtOffset(remaining, LogicalDirection.Forward)
                           ?? run.ContentEnd;
                }

                remaining -= length;
            }
        }

        return last ?? document.ContentEnd;
    }

    public static int CountChips(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var count = 0;
        foreach (var inline in EnumerateInlines(document))
        {
            if (inline is InlineUIContainer container && container.Child is ComposerFileChip)
            {
                count++;
            }
        }

        return count;
    }

    public static InlineUIContainer CreateChipContainer(ComposerMentionSpan mention)
    {
        var tooltip = mention.Kind == ComposerMentionKind.File
            ? mention.RelativePath
            : mention.InsertText.TrimEnd();
        var chip = new ComposerFileChip
        {
            InsertText = mention.InsertText,
            FileName = mention.DisplayName,
            IconKind = mention.IconKind,
            MentionKind = mention.Kind,
            ToolTipPath = tooltip,
            VerticalAlignment = VerticalAlignment.Center
        };

        return new InlineUIContainer(chip)
        {
            BaselineAlignment = BaselineAlignment.Center
        };
    }

    private static void AppendInline(StringBuilder builder, Inline inline)
    {
        if (inline is InlineUIContainer container && container.Child is ComposerFileChip chip)
        {
            builder.Append(chip.InsertText);
            return;
        }

        if (inline is LineBreak)
        {
            builder.Append('\n');
            return;
        }

        if (inline is Run run)
        {
            builder.Append((run.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n'));
        }
    }

    private static void AppendPlainText(Paragraph paragraph, string chunk)
    {
        var start = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != '\n')
            {
                continue;
            }

            if (i > start)
            {
                paragraph.Inlines.Add(new Run(chunk[start..i]));
            }

            paragraph.Inlines.Add(new LineBreak());
            start = i + 1;
        }

        if (start < chunk.Length)
        {
            paragraph.Inlines.Add(new Run(chunk[start..]));
        }
    }

    private static Paragraph EnsureSingleParagraph(FlowDocument document)
    {
        if (document.Blocks.FirstBlock is Paragraph existing)
        {
            while (document.Blocks.LastBlock != existing)
            {
                document.Blocks.Remove(document.Blocks.LastBlock);
            }

            existing.Margin = new Thickness(0);
            return existing;
        }

        document.Blocks.Clear();
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        document.Blocks.Add(paragraph);
        return paragraph;
    }

    private static IEnumerable<Inline> EnumerateInlines(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            if (block is Paragraph paragraph)
            {
                foreach (var inline in paragraph.Inlines)
                {
                    yield return inline;
                }
            }
        }
    }

    private static bool IsEmbeddedAtSign(char previous) =>
        char.IsLetterOrDigit(previous) || previous is '.' or '_' or '-';
}
