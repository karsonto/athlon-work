using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

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
            ".pdf" => ExtractPdfText(path),
            _ => throw new NotSupportedException($"不支持的知识库文件类型：{extension}")
        };

        text = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "未能从 PDF 中抽取到可索引文本。该 PDF 可能是扫描件/图片型文件，或缺少可解析的文字映射；请先使用 OCR 生成带文本层的 PDF，或上传可复制文字的文档。"
                : "未能从文件中抽取到可索引文本。");
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

    private static string ExtractPdfText(string path)
    {
        var builder = new StringBuilder();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            if (string.IsNullOrWhiteSpace(page.Text))
            {
                continue;
            }

            builder.AppendLine($"# Page {page.Number}");
            builder.AppendLine(page.Text);
            builder.AppendLine();
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
