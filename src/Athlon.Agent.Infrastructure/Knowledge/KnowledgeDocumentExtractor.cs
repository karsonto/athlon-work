using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using UglyToad.PdfPig;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed record ExtractedKnowledgeDocument(string Text, string Title);

public sealed class KnowledgeDocumentExtractor(
    AppSettings settings,
    IKnowledgePageOcr pageOcr)
{
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
        var minChars = Math.Max(1, ocr.MinCharsPerPage);
        var batchSize = Math.Clamp(ocr.BatchSize <= 0 ? 3 : ocr.BatchSize, 1, 8);
        var pdfBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var baseName = Path.GetFileNameWithoutExtension(path);
        var builder = new StringBuilder();
        var pending = new List<KnowledgeOcrPageImage>();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "athlon-knowledge-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

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
                if (pageText.Length >= minChars)
                {
                    AppendPage(builder, pageNumber, pageText);
                    Report(progress, pageIndex, pageCount, $"已抽取第 {pageNumber} 页文本");
                    continue;
                }

                var image = await PdfPageJpegRenderer
                    .RenderPageAsync(pdfBytes, baseName, pageNumber, tempDirectory, ocr.RenderDpi, cancellationToken)
                    .ConfigureAwait(false);
                pending.Add(new KnowledgeOcrPageImage(pageNumber, image));
                Report(progress, pageIndex, pageCount, $"第 {pageNumber} 页待 OCR（队列 {pending.Count}/{batchSize}）");

                if (pending.Count >= batchSize)
                {
                    await FlushOcrBatchAsync(builder, pending, progress, pageIndex, pageCount, cancellationToken)
                        .ConfigureAwait(false);
                    pending.Clear();
                }
            }

            if (pending.Count > 0)
            {
                await FlushOcrBatchAsync(builder, pending, progress, pageIndex, pageCount, cancellationToken)
                    .ConfigureAwait(false);
                pending.Clear();
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }

        return builder.ToString();
    }

    private async Task FlushOcrBatchAsync(
        StringBuilder builder,
        List<KnowledgeOcrPageImage> batch,
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
        Report(progress, processedPages, totalPages, $"OCR 第 {first}–{last} 页（{batch.Count} 张）");

        var recognized = await pageOcr
            .RecognizePagesAsync(batch, cancellationToken)
            .ConfigureAwait(false);

        foreach (var page in batch)
        {
            if (recognized.TryGetValue(page.PageNumber, out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                AppendPage(builder, page.PageNumber, DocumentTextExtraction.NormalizeText(text));
            }
        }
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
}
