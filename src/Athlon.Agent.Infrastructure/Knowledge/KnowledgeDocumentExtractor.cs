using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed record ExtractedKnowledgeDocument(string Text, string Title);

public sealed class KnowledgeDocumentExtractor(
    AppSettings settings,
    IKnowledgePageOcr pageOcr,
    IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForContext("KnowledgeExtractor");

    public Task<ExtractedKnowledgeDocument> ExtractAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ExtractAsync(path, cancellationToken, progress: null);

    public async Task<ExtractedKnowledgeDocument> ExtractAsync(
        string path,
        CancellationToken cancellationToken,
        IProgress<KnowledgeIndexingProgress>? progress)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        string text;
        try
        {
            if (extension == ".pdf" && settings.Knowledge.Ocr.Enabled)
            {
                text = await ExtractPdfWithOptionalOcrAsync(path, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                text = await DocumentTextExtraction.ExtractRawTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (NotSupportedException) when (extension is not ".xls")
        {
            throw new NotSupportedException($"不支持的知识库文件类型：{extension}");
        }

        text = DocumentTextExtraction.NormalizeText(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? settings.Knowledge.Ocr.Enabled
                    ? "未能从 PDF 中抽取到可索引文本（含视觉 OCR）。请确认聊天模型支持图片输入，或上传可复制文字的文档。"
                    : "未能从 PDF 中抽取到可索引文本。该 PDF 可能是扫描件/图片型文件，或缺少可解析的文字映射；请先使用 OCR 生成带文本层的 PDF，或上传可复制文字的文档。"
                : "未能从文件中抽取到可索引文本。");
        }

        return new ExtractedKnowledgeDocument(text, Path.GetFileNameWithoutExtension(path));
    }

    private async Task<string> ExtractPdfWithOptionalOcrAsync(
        string path,
        IProgress<KnowledgeIndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var ocr = settings.Knowledge.Ocr;
        var batchSize = Math.Clamp(ocr.BatchSize <= 0 ? 3 : ocr.BatchSize, 1, 8);
        var pdfBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var baseName = Path.GetFileNameWithoutExtension(path);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "athlon-knowledge-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var pageWorks = new List<PageWork>();
        var pendingOcr = new List<PendingOcrItem>();
        var nextSlot = 1;

        try
        {
            using var document = PdfDocument.Open(pdfBytes);
            var pageCount = document.NumberOfPages;
            var pageIndex = 0;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageIndex++;
                var pageNumber = page.Number;
                var pageText = DocumentTextExtraction.NormalizeText(page.Text ?? "");
                var exported = ExportPageImages(page, baseName, pageNumber, tempDirectory);
                var work = new PageWork(pageNumber, pageText);

                if (exported.Count > 0)
                {
                    foreach (var (imageIndex, attachment) in exported)
                    {
                        pendingOcr.Add(new PendingOcrItem(nextSlot++, pageNumber, imageIndex, attachment));
                    }

                    Report(progress, pageIndex, pageCount, $"第 {pageNumber} 页：文字 + {exported.Count} 张图待 OCR");
                }
                else if (string.IsNullOrWhiteSpace(pageText))
                {
                    var fallback = await PdfPageJpegRenderer
                        .RenderPageAsync(pdfBytes, baseName, pageNumber, tempDirectory, ocr.RenderDpi, cancellationToken)
                        .ConfigureAwait(false);
                    pendingOcr.Add(new PendingOcrItem(nextSlot++, pageNumber, 0, fallback));
                    Report(progress, pageIndex, pageCount, $"第 {pageNumber} 页无字无图，整页 OCR");
                }
                else
                {
                    Report(progress, pageIndex, pageCount, $"第 {pageNumber} 页仅文字层");
                }

                pageWorks.Add(work);

                while (pendingOcr.Count >= batchSize)
                {
                    var batch = pendingOcr.GetRange(0, batchSize);
                    pendingOcr.RemoveRange(0, batchSize);
                    await FlushOcrBatchAsync(batch, pageWorks, progress, pageIndex, pageCount, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (pendingOcr.Count > 0)
            {
                await FlushOcrBatchAsync(pendingOcr, pageWorks, progress, pageIndex, pageCount, cancellationToken)
                    .ConfigureAwait(false);
                pendingOcr.Clear();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }

        var builder = new StringBuilder();
        foreach (var work in pageWorks.OrderBy(p => p.PageNumber))
        {
            var merged = MergePageContent(work);
            AppendPage(builder, work.PageNumber, merged);
        }

        return builder.ToString();
    }

    private List<(int ImageIndex, ImageAttachment Attachment)> ExportPageImages(
        Page page,
        string baseName,
        int pageNumber,
        string tempDirectory)
    {
        var result = new List<(int, ImageAttachment)>();
        IReadOnlyList<IPdfImage> images;
        try
        {
            images = page.GetImages().ToArray();
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to enumerate images on PDF page {Page}: {Message}", pageNumber, ex.Message);
            return result;
        }

        var imageIndex = 0;
        foreach (var image in images)
        {
            imageIndex++;
            if (!PdfEmbeddedImageExporter.IsLargeEnough(image))
            {
                continue;
            }

            try
            {
                var attachment = PdfEmbeddedImageExporter.TryExport(
                    image,
                    baseName,
                    pageNumber,
                    imageIndex,
                    tempDirectory);
                if (attachment is null)
                {
                    _logger.Warning(
                        "Skipping undecodable image {ImageIndex} on PDF page {Page}",
                        imageIndex,
                        pageNumber);
                    continue;
                }

                result.Add((imageIndex, attachment));
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "Failed to export image {ImageIndex} on PDF page {Page}: {Message}",
                    imageIndex,
                    pageNumber,
                    ex.Message);
            }
        }

        return result;
    }

    private async Task FlushOcrBatchAsync(
        List<PendingOcrItem> batch,
        List<PageWork> pageWorks,
        IProgress<KnowledgeIndexingProgress>? progress,
        int processedPages,
        int totalPages,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var first = batch[0].PageNumber;
        var last = batch[^1].PageNumber;
        Report(progress, processedPages, totalPages, $"OCR 图批（页 {first}–{last}，{batch.Count} 张）");

        var request = batch
            .Select(item => new KnowledgeOcrPageImage(item.Slot, item.Attachment))
            .ToArray();
        var recognized = await pageOcr
            .RecognizePagesAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var byPage = pageWorks.ToDictionary(p => p.PageNumber);
        foreach (var item in batch)
        {
            if (!recognized.TryGetValue(item.Slot, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!byPage.TryGetValue(item.PageNumber, out var work))
            {
                continue;
            }

            work.OcrFragments.Add(DocumentTextExtraction.NormalizeText(text));
        }
    }

    private static string MergePageContent(PageWork work)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(work.Text))
        {
            builder.AppendLine(work.Text.Trim());
        }

        foreach (var fragment in work.OcrFragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("---");
                builder.AppendLine();
            }

            builder.AppendLine(fragment.Trim());
        }

        return builder.ToString().Trim();
    }

    private static void AppendPage(StringBuilder builder, int pageNumber, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        builder.AppendLine($"# Page {pageNumber}");
        builder.AppendLine(text.Trim());
        builder.AppendLine();
    }

    private static void Report(
        IProgress<KnowledgeIndexingProgress>? progress,
        int processed,
        int total,
        string message)
    {
        if (progress is null || total <= 0)
        {
            return;
        }

        var percent = 10 + (processed / (double)total * 14);
        progress.Report(new KnowledgeIndexingProgress(
            "抽取文本",
            message,
            processed,
            total,
            Math.Clamp(percent, 10, 24)));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of OCR temp JPEGs.
        }
    }

    private sealed class PageWork(int pageNumber, string text)
    {
        public int PageNumber { get; } = pageNumber;
        public string Text { get; } = text;
        public List<string> OcrFragments { get; } = [];
    }

    private sealed record PendingOcrItem(
        int Slot,
        int PageNumber,
        int ImageIndex,
        ImageAttachment Attachment);
}
