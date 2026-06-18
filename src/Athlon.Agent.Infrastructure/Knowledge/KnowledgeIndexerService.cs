using System.Security.Cryptography;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class KnowledgeIndexerService(
    IAppPathProvider paths,
    AppSettings settings,
    IKnowledgeStore store,
    IEmbeddingClient embeddingClient,
    KnowledgeDocumentExtractor extractor,
    KnowledgeChunker chunker,
    IAppLogger logger) : IKnowledgeIndexer
{
    private readonly IAppLogger _logger = logger.ForContext("KnowledgeIndexer");

    private string KnowledgeRoot => Path.Combine(paths.RootPath, settings.Knowledge.DirectoryName);

    public async Task<KnowledgeDocument> ImportDocumentAsync(
        string moduleId,
        string sourcePath,
        CancellationToken cancellationToken = default,
        IProgress<KnowledgeIndexingProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            throw new ArgumentException("moduleId is required.", nameof(moduleId));
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("知识库导入文件不存在。", sourcePath);
        }

        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var documentId = Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileName(sourcePath);
        Report(progress, "准备", $"准备导入 {fileName}", 0, 1, 0);
        var safeFileName = $"{documentId}-{FileNameSanitizer.Sanitize(fileName)}";
        var originalsDir = Path.Combine(KnowledgeRoot, "originals", moduleId);
        Directory.CreateDirectory(originalsDir);
        var originalPath = Path.Combine(originalsDir, safeFileName);
        File.Copy(sourcePath, originalPath, overwrite: true);
        Report(progress, "准备", $"已复制 {fileName}", 1, 1, 5);

        var document = new KnowledgeDocument
        {
            Id = documentId,
            ModuleId = moduleId,
            FileName = fileName,
            FileType = Path.GetExtension(sourcePath).ToLowerInvariant(),
            OriginalPath = originalPath,
            ContentHash = await ComputeHashAsync(originalPath, cancellationToken).ConfigureAwait(false),
            Status = KnowledgeDocumentStatus.Pending
        };

        await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        await ReindexDocumentAsync(document.Id, cancellationToken, progress).ConfigureAwait(false);
        return await store.GetDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false) ?? document;
    }

    public async Task ReindexDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default,
        IProgress<KnowledgeIndexingProgress>? progress = null)
    {
        var document = await store.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"知识库文档不存在：{documentId}");

        try
        {
            Report(progress, "抽取文本", $"正在抽取 {document.FileName}", 0, 1, 10);
            document.Status = KnowledgeDocumentStatus.Extracting;
            document.LastError = "";
            await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);

            var extracted = await extractor.ExtractAsync(document.OriginalPath, cancellationToken).ConfigureAwait(false);
            var extractedDir = Path.Combine(KnowledgeRoot, "extracted", document.ModuleId);
            Directory.CreateDirectory(extractedDir);
            document.ExtractedPath = Path.Combine(extractedDir, $"{document.Id}.md");
            await AtomicFile.WriteAllTextAsync(document.ExtractedPath, extracted.Text, cancellationToken).ConfigureAwait(false);

            Report(progress, "切片", $"正在切分 {document.FileName}", 0, 1, 25);
            document.Status = KnowledgeDocumentStatus.Chunking;
            await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            var chunks = chunker.Chunk(document.Id, document.ModuleId, extracted.Text, extracted.Title).ToList();

            Report(progress, "向量化", $"准备向量化 {chunks.Count} 个切片", 0, chunks.Count, 35);
            document.Status = KnowledgeDocumentStatus.Embedding;
            document.ChunkCount = chunks.Count;
            await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);

            var embeddings = new List<EmbeddingVector>(chunks.Count);
            var processed = 0;
            var batchSize = Math.Max(1, settings.Knowledge.Embedding.BatchSize);
            foreach (var batch in chunks.Chunk(batchSize))
            {
                var batchEmbeddings = await embeddingClient
                    .EmbedAsync(batch.Select(chunk => chunk.Content).ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                embeddings.AddRange(batchEmbeddings);
                processed += batch.Length;
                var percent = chunks.Count == 0
                    ? 90
                    : 35 + (processed / (double)chunks.Count * 55);
                Report(progress, "向量化", $"正在向量化 {document.FileName}：{processed}/{chunks.Count} 个切片", processed, chunks.Count, percent);
            }

            if (embeddings.Count != chunks.Count)
            {
                throw new InvalidOperationException($"Embedding 返回数量不匹配。Expected {chunks.Count}, got {embeddings.Count}.");
            }

            for (var i = 0; i < chunks.Count; i++)
            {
                chunks[i].Embedding = embeddings[i].Vector;
                chunks[i].EmbeddingModel = settings.Knowledge.Embedding.Model;
                chunks[i].EmbeddingDimension = embeddings[i].Vector.Length;
            }

            Report(progress, "写入索引", $"正在写入 {document.FileName} 的索引", chunks.Count, chunks.Count, 95);
            await store.ReplaceChunksAsync(document.Id, chunks, cancellationToken).ConfigureAwait(false);
            document.Status = KnowledgeDocumentStatus.Indexed;
            document.ChunkCount = chunks.Count;
            document.LastError = "";
            await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            Report(progress, "完成", $"{document.FileName} 索引完成", chunks.Count, chunks.Count, 100);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning("Knowledge document indexing failed for {DocumentId}: {Message}", document.Id, exception.Message);
            document.Status = KnowledgeDocumentStatus.Failed;
            document.LastError = exception.Message;
            await store.SaveDocumentAsync(document, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Report(
        IProgress<KnowledgeIndexingProgress>? progress,
        string stage,
        string message,
        int processed,
        int total,
        double percent)
    {
        progress?.Report(new KnowledgeIndexingProgress(
            stage,
            message,
            processed,
            total,
            Math.Clamp(percent, 0, 100)));
    }
}
