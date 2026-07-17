using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Athlon.Agent.Core;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Athlon.Agent.Infrastructure;

public sealed class ChatDocumentAttachmentExtractor : IChatDocumentAttachmentExtractor
{
    private const int MaxPreviewDimension = 1400;
    private const int JpegQuality = 82;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".xls", ".csv", ".pptx", ".txt", ".md"
    };

    public bool IsImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && ImageExtensions.Contains(extension);
    }

    public bool IsLegacyPresentation(string path) =>
        Path.GetExtension(path).Equals(".ppt", StringComparison.OrdinalIgnoreCase);

    public bool IsSupportedDocument(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && DocumentExtensions.Contains(extension);
    }

    public async Task<ChatDocumentExtractionResult> ExtractAllVisualAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Attachment file was not found.", path);
        }

        if (IsLegacyPresentation(path))
        {
            throw new NotSupportedException("暂不支持老式 \".ppt\" 文件，请另存为 \".pptx\" 后上传");
        }

        if (!IsSupportedDocument(path))
        {
            throw new NotSupportedException($"不支持的文件类型：{Path.GetExtension(path)}");
        }

        DocumentTextExtraction.EnsureFileSizeWithinLimit(path);
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "athlon-chat-attachments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await DocumentTextExtraction.ExtractRawTextAsync(path, cancellationToken).ConfigureAwait(false);
            var truncated = DocumentTextExtraction.TruncateForChat(text);
            var visuals = extension switch
            {
                ".pdf" => await RenderAllPdfPagesAsync(path, fileName, tempDirectory, cancellationToken)
                    .ConfigureAwait(false),
                ".docx" => ExtractDocxEmbeddedImages(path, fileName, tempDirectory),
                ".pptx" => ExtractPptxEmbeddedImages(path, fileName, tempDirectory),
                _ => Array.Empty<ImageAttachment>()
            };

            // Keep temp files alive for PersistPendingImages to copy; callers own lifetime via LocalPath.
            return new ChatDocumentExtractionResult(fileName, truncated, visuals);
        }
        catch
        {
            TryDeleteDirectory(tempDirectory);
            throw;
        }
    }

    private static async Task<IReadOnlyList<ImageAttachment>> RenderAllPdfPagesAsync(
        string path,
        string sourceFileName,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var pdfBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var pageCount = 0;
        using (var pdf = PdfDocument.Open(pdfBytes))
        {
            pageCount = pdf.NumberOfPages;
        }

        if (pageCount <= 0)
        {
            return Array.Empty<ImageAttachment>();
        }

        var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
        var attachments = new List<ImageAttachment>(pageCount);

        // PDFtoImage page index is 0-based.
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = pageIndex + 1;
            var outputPath = Path.Combine(tempDirectory, $"{baseName}-p{pageNumber:D4}.jpg");
            var dpi = EstimateDpiForPage(pdfBytes, pageNumber);
            Conversion.SaveJpeg(
                outputPath,
                pdfBytes,
                page: pageIndex,
                password: null,
                options: new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true));

            // Re-encode at planned quality / max dimension when needed.
            ResizeJpegInPlace(outputPath, MaxPreviewDimension, JpegQuality);

            attachments.Add(new ImageAttachment(
                $"{baseName}-第{pageNumber}页预览.jpg",
                "image/jpeg",
                DataUrl: null,
                LocalPath: outputPath));
        }

        return attachments;
    }

    private static int EstimateDpiForPage(byte[] pdfBytes, int pageNumber)
    {
        try
        {
            using var document = PdfDocument.Open(pdfBytes);
            var page = document.GetPage(pageNumber);
            var width = page.Width;
            var height = page.Height;
            var maxPoint = Math.Max(width, height);
            if (maxPoint <= 0)
            {
                return 120;
            }

            // Aim for ~MaxPreviewDimension CSS pixels across the longer edge.
            var dpi = (int)Math.Clamp(72.0 * MaxPreviewDimension / maxPoint, 72, 144);
            return dpi;
        }
        catch
        {
            return 120;
        }
    }

    private static void ResizeJpegInPlace(string path, int maxDimension, int quality)
    {
        using var input = SKBitmap.Decode(path);
        if (input is null)
        {
            return;
        }

        var scale = Math.Min(1.0, maxDimension / (double)Math.Max(input.Width, input.Height));
        SKBitmap bitmap = input;
        SKBitmap? scaled = null;
        try
        {
            if (scale < 0.999)
            {
                var width = Math.Max(1, (int)Math.Round(input.Width * scale));
                var height = Math.Max(1, (int)Math.Round(input.Height * scale));
                scaled = input.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
                if (scaled is not null)
                {
                    bitmap = scaled;
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data is null)
            {
                return;
            }

            using var output = File.Open(path, FileMode.Create, FileAccess.Write);
            data.SaveTo(output);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    private static IReadOnlyList<ImageAttachment> ExtractDocxEmbeddedImages(
        string path,
        string sourceFileName,
        string tempDirectory)
    {
        using var archive = ZipFile.OpenRead(path);
        var mediaEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Name))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return WriteZipMediaAttachments(mediaEntries, sourceFileName, tempDirectory, "插图");
    }

    private static IReadOnlyList<ImageAttachment> ExtractPptxEmbeddedImages(
        string path,
        string sourceFileName,
        string tempDirectory)
    {
        using var archive = ZipFile.OpenRead(path);
        var slidePaths = archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => Regex.IsMatch(name, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(name =>
            {
                var match = Regex.Match(name, @"slide(\d+)\.xml", RegexOptions.IgnoreCase);
                return match.Success ? int.Parse(match.Groups[1].Value) : 0;
            })
            .ToList();

        var mediaPaths = new List<string>();
        foreach (var slidePath in slidePaths)
        {
            var slideEntry = archive.GetEntry(slidePath);
            if (slideEntry is null)
            {
                continue;
            }

            using var slideStream = slideEntry.Open();
            XDocument slideXml;
            try
            {
                slideXml = XDocument.Load(slideStream);
            }
            catch
            {
                continue;
            }

            var embedIds = slideXml.Descendants()
                .Where(node => node.Name.LocalName == "blip")
                .SelectMany(node => node.Attributes().Where(attribute =>
                    attribute.Name.LocalName is "embed" or "link"))
                .Select(attribute => attribute.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (embedIds.Count == 0)
            {
                continue;
            }

            var relPath = Regex.Replace(
                slidePath,
                @"slides/(slide\d+\.xml)$",
                "slides/_rels/$1.rels",
                RegexOptions.IgnoreCase);
            var relEntry = archive.GetEntry(relPath);
            if (relEntry is null)
            {
                continue;
            }

            using var relStream = relEntry.Open();
            XDocument relXml;
            try
            {
                relXml = XDocument.Load(relStream);
            }
            catch
            {
                continue;
            }

            var relMap = relXml.Descendants()
                .Where(node => node.Name.LocalName == "Relationship")
                .Select(node => (
                    Id: (string?)node.Attribute("Id"),
                    Target: (string?)node.Attribute("Target")))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Id) && !string.IsNullOrWhiteSpace(pair.Target))
                .ToDictionary(pair => pair.Id!, pair => pair.Target!, StringComparer.Ordinal);

            foreach (var embedId in embedIds)
            {
                if (!relMap.TryGetValue(embedId, out var target) || string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                var mediaPath = ResolveZipPath(slidePath, target);
                if (!mediaPaths.Contains(mediaPath, StringComparer.OrdinalIgnoreCase))
                {
                    mediaPaths.Add(mediaPath);
                }
            }
        }

        var mediaEntries = mediaPaths
            .Select(mediaPath => archive.GetEntry(mediaPath))
            .Where(entry => entry is not null)
            .Cast<ZipArchiveEntry>()
            .ToList();

        return WriteZipMediaAttachments(mediaEntries, sourceFileName, tempDirectory, "插图");
    }

    private static IReadOnlyList<ImageAttachment> WriteZipMediaAttachments(
        IReadOnlyList<ZipArchiveEntry> mediaEntries,
        string sourceFileName,
        string tempDirectory,
        string label)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
        var attachments = new List<ImageAttachment>();
        var index = 0;
        foreach (var entry in mediaEntries)
        {
            index += 1;
            var extension = Path.GetExtension(entry.Name);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bin";
            }

            var mime = GuessMimeType(extension);
            if (string.IsNullOrWhiteSpace(mime) || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var outputName = $"{baseName}-{label}{index}{extension}";
            var outputPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}{extension}");
            using (var input = entry.Open())
            using (var output = File.Create(outputPath))
            {
                input.CopyTo(output);
            }

            attachments.Add(new ImageAttachment(
                outputName,
                mime,
                DataUrl: null,
                LocalPath: outputPath));
        }

        return attachments;
    }

    private static string ResolveZipPath(string baseFilePath, string relativePath)
    {
        var baseParts = baseFilePath.Replace('\\', '/').Split('/');
        var stack = new List<string>(baseParts.Take(baseParts.Length - 1));
        foreach (var part in relativePath.Replace('\\', '/').Split('/'))
        {
            if (string.IsNullOrEmpty(part) || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    private static string GuessMimeType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => string.Empty
        };

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
