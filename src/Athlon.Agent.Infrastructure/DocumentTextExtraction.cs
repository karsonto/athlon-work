using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Shared document text extraction used by knowledge indexing and chat attachments.
/// </summary>
public static class DocumentTextExtraction
{
    public const long MaxAttachmentFileSizeBytes = 12L * 1024 * 1024;
    public const int MaxAttachmentCharacters = 80_000;

    public static void EnsureFileSizeWithinLimit(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("File was not found.", path);
        }

        if (info.Length > MaxAttachmentFileSizeBytes)
        {
            throw new InvalidOperationException(
                $"文件大小超过 {MaxAttachmentFileSizeBytes / 1024 / 1024} MB 限制，请拆分后重试");
        }
    }

    public static async Task<string> ExtractRawTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".txt" or ".md" or ".csv" => await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false),
            ".docx" => ExtractOpenXmlText(
                path,
                entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)),
            ".xlsx" => ExtractOpenXmlText(
                path,
                entry =>
                    entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)),
            ".xls" => throw new NotSupportedException("暂不支持老式 \".xls\" 文件，请另存为 \".xlsx\" 或 \".csv\" 后上传"),
            ".pptx" => ExtractOpenXmlText(
                path,
                entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)),
            ".pdf" => ExtractPdfText(path),
            _ => throw new NotSupportedException($"不支持的文件类型：{extension}")
        };
    }

    public static string NormalizeText(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = Regex.Replace(normalized, "[\t ]+", " ");
        normalized = Regex.Replace(normalized, "\n{3,}", "\n\n");
        return normalized.Trim();
    }

    public static string TruncateForChat(string text)
    {
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "[未提取到可读文本]";
        }

        if (normalized.Length <= MaxAttachmentCharacters)
        {
            return normalized;
        }

        return string.Join(
            "\n",
            [
                normalized[..MaxAttachmentCharacters],
                string.Empty,
                $"[内容过长，已截断后发送给模型] 原始长度约 {normalized.Length:N0} 个字符。"
            ]);
    }

    public static string ExtractOpenXmlText(string path, Func<ZipArchiveEntry, bool> includeEntry)
    {
        using var archive = ZipFile.OpenRead(path);
        var builder = new StringBuilder();
        foreach (var entry in archive.Entries.Where(includeEntry).OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = entry.Open();
            try
            {
                var document = XDocument.Load(stream);
                var text = string.Join(
                    " ",
                    document.DescendantNodes().OfType<XText>().Select(node => node.Value.Trim()).Where(value => value.Length > 0));
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

    public static string ExtractPdfText(string path)
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
}
