namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed record ExtractedKnowledgeDocument(string Text, string Title);

public sealed class KnowledgeDocumentExtractor
{
    public async Task<ExtractedKnowledgeDocument> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        string text;
        try
        {
            text = await DocumentTextExtraction.ExtractRawTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException) when (extension is not ".xls")
        {
            throw new NotSupportedException($"不支持的知识库文件类型：{extension}");
        }

        text = DocumentTextExtraction.NormalizeText(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "未能从 PDF 中抽取到可索引文本。该 PDF 可能是扫描件/图片型文件，或缺少可解析的文字映射；请先使用 OCR 生成带文本层的 PDF，或上传可复制文字的文档。"
                : "未能从文件中抽取到可索引文本。");
        }

        return new ExtractedKnowledgeDocument(text, Path.GetFileNameWithoutExtension(path));
    }
}
