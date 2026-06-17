using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed record ExtractedKnowledgeDocument(string Text, string Title);

public sealed class KnowledgeDocumentExtractor
{
    public async Task<ExtractedKnowledgeDocument> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var text = extension switch
        {
            ".txt" or ".md" or ".csv" => await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false),
            ".docx" => ExtractOpenXmlText(path, entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)),
            ".xlsx" => ExtractOpenXmlText(path, entry =>
                entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)),
            ".pptx" => ExtractOpenXmlText(path, entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)),
            ".pdf" => ExtractBasicPdfText(path),
            _ => throw new NotSupportedException($"不支持的知识库文件类型：{extension}")
        };

        text = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("未能从文件中抽取到可索引文本。");
        }

        return new ExtractedKnowledgeDocument(text, Path.GetFileNameWithoutExtension(path));
    }

    private static string ExtractOpenXmlText(string path, Func<ZipArchiveEntry, bool> includeEntry)
    {
        using var archive = ZipFile.OpenRead(path);
        var builder = new StringBuilder();
        foreach (var entry in archive.Entries.Where(includeEntry).OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = entry.Open();
            try
            {
                var document = XDocument.Load(stream);
                var text = string.Join(" ", document.DescendantNodes().OfType<XText>().Select(node => node.Value.Trim()).Where(value => value.Length > 0));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine($"# {Path.GetFileNameWithoutExtension(entry.FullName)}");
                    builder.AppendLine(text);
                    builder.AppendLine();
                }
            }
            catch
            {
                // Ignore malformed XML parts and continue with the rest of the package.
            }
        }

        return builder.ToString();
    }

    private static string ExtractBasicPdfText(string path)
    {
        // Minimal PDF fallback: extract literal strings from content streams.
        // This is intentionally conservative; failed or image-only PDFs surface as indexing failures.
        var bytes = File.ReadAllBytes(path);
        var raw = Encoding.Latin1.GetString(bytes);
        var builder = new StringBuilder();
        foreach (Match match in Regex.Matches(raw, @"\((?<text>(?:\\.|[^\\)]){2,})\)"))
        {
            var text = match.Groups["text"].Value
                .Replace("\\(", "(", StringComparison.Ordinal)
                .Replace("\\)", ")", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal);
            if (text.Any(char.IsLetterOrDigit))
            {
                builder.AppendLine(text);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeText(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = Regex.Replace(normalized, "[\t ]+", " ");
        normalized = Regex.Replace(normalized, "\n{3,}", "\n\n");
        return normalized.Trim();
    }
}
