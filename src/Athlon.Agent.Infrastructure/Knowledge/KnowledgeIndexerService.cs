using System.Security.Cryptography;
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
    IKnowledgeGraphExtractor graphExtractor,
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
        var documentId = IdGen.NewId();
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

        try
        {
            var indexed = await BuildIndexedDocumentAsync(document, progress, cancellationToken).ConfigureAwait(false);
            var committed = await store.CommitIndexedDocumentAsync(indexed.Document, indexed.Chunks, cancellationToken, indexed.Graph).ConfigureAwait(false);
            Report(progress, "完成", $"{document.FileName} 索引完成", indexed.Chunks.Count, indexed.Chunks.Count, 100);
            return committed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning("Knowledge document import failed for {DocumentId}: {Message}", document.Id, exception.Message);
            CleanupImportArtifacts(document);
            throw;
        }
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
            var indexed = await BuildIndexedDocumentAsync(document, progress, cancellationToken).ConfigureAwait(false);
            await store.CommitIndexedDocumentAsync(indexed.Document, indexed.Chunks, cancellationToken, indexed.Graph).ConfigureAwait(false);
            Report(progress, "完成", $"{document.FileName} 索引完成", indexed.Chunks.Count, indexed.Chunks.Count, 100);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning("Knowledge document reindex failed for {DocumentId}: {Message}", document.Id, exception.Message);
            throw;
        }
    }

    public async Task<KnowledgeGraphRebuildResult> RebuildModuleGraphAsync(
        string moduleId,
        CancellationToken cancellationToken = default,
        IProgress<KnowledgeIndexingProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            throw new ArgumentException("moduleId is required.", nameof(moduleId));
        }

        var documents = (await store.ListDocumentsAsync(moduleId, cancellationToken).ConfigureAwait(false))
            .Where(document => document.Status == KnowledgeDocumentStatus.Indexed)
            .ToArray();
        var succeeded = 0;
        var failed = 0;
        for (var index = 0; index < documents.Length; index++)
        {
            var document = documents[index];
            try
            {
                Report(progress, "抽取图谱", $"正在重建 {document.FileName}", index, documents.Length, index / (double)Math.Max(1, documents.Length) * 100);
                var chunks = await store.ListDocumentChunksAsync(document.Id, cancellationToken).ConfigureAwait(false);
                var graph = await BuildGraphCommitAsync(chunks, cancellationToken).ConfigureAwait(false);
                await store.ReplaceDocumentGraphAsync(document.Id, graph, cancellationToken).ConfigureAwait(false);
                succeeded++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                _logger.Warning("Knowledge graph rebuild failed for {DocumentId}: {Message}", document.Id, exception.Message);
                await store.SetDocumentGraphStatusAsync(document.Id, KnowledgeGraphStatus.Failed, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
            }

            Report(
                progress,
                "抽取图谱",
                $"{document.FileName} 已处理",
                index + 1,
                documents.Length,
                (index + 1) / (double)documents.Length * 100);
        }

        Report(progress, "完成", "图谱重建结束", documents.Length, documents.Length, 100);
        return new KnowledgeGraphRebuildResult(succeeded, failed);
    }

    private async Task<(KnowledgeDocument Document, IReadOnlyList<KnowledgeChunk> Chunks, DocumentGraphCommit Graph)> BuildIndexedDocumentAsync(
        KnowledgeDocument document,
        IProgress<KnowledgeIndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "抽取文本", $"正在抽取 {document.FileName}", 0, 1, 10);
        var extracted = await extractor
            .ExtractAsync(document.OriginalPath, cancellationToken, progress)
            .ConfigureAwait(false);
        var extractedDir = Path.Combine(KnowledgeRoot, "extracted", document.ModuleId);
        Directory.CreateDirectory(extractedDir);
        document.ExtractedPath = Path.Combine(extractedDir, $"{document.Id}.md");
        await AtomicFile.WriteAllTextAsync(document.ExtractedPath, extracted.Text, cancellationToken).ConfigureAwait(false);

        Report(progress, "切片", $"正在切分 {document.FileName}", 0, 1, 25);
        var chunks = chunker.Chunk(document.Id, document.ModuleId, extracted.Text, extracted.Title).ToList();

        Report(progress, "向量化", $"准备向量化 {chunks.Count} 个切片", 0, chunks.Count, 35);
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

        document.Status = KnowledgeDocumentStatus.Indexed;
        document.ChunkCount = chunks.Count;
        document.LastError = "";

        DocumentGraphCommit graph;
        var graphSettings = settings.Knowledge.Graph ??= new KnowledgeGraphSettings();
        if (!graphSettings.Enabled)
        {
            document.GraphStatus = KnowledgeGraphStatus.Skipped;
            document.GraphError = "";
            graph = DocumentGraphCommit.Skipped();
        }
        else
        {
            try
            {
                Report(progress, "抽取图谱", $"正在抽取 {document.FileName} 的实体和关系", 0, 1, 91);
                graph = await BuildGraphCommitAsync(chunks, cancellationToken).ConfigureAwait(false);
                document.GraphStatus = graph.Status;
                document.GraphError = "";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warning("Knowledge graph extraction failed for {DocumentId}: {Message}", document.Id, exception.Message);
                document.GraphStatus = KnowledgeGraphStatus.Failed;
                document.GraphError = exception.Message;
                graph = DocumentGraphCommit.Failed(exception.Message);
            }
        }

        Report(progress, "写入索引", $"正在写入 {document.FileName} 的索引", chunks.Count, chunks.Count, 95);
        return (document, chunks, graph);
    }

    private async Task<DocumentGraphCommit> BuildGraphCommitAsync(
        IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken)
    {
        var graphSettings = settings.Knowledge.Graph ??= new KnowledgeGraphSettings();
        var extraction = await graphExtractor
            .ExtractAsync(chunks, graphSettings.NormalizedEntityTypes(), cancellationToken)
            .ConfigureAwait(false);
        var graph = KnowledgeGraphMerger.Build(extraction);
        await EmbedGraphEntitiesAsync(graph, cancellationToken).ConfigureAwait(false);
        return graph;
    }

    private async Task EmbedGraphEntitiesAsync(DocumentGraphCommit graph, CancellationToken cancellationToken)
    {
        if (graph.Entities.Count == 0)
        {
            return;
        }

        var texts = graph.Entities
            .Select(entity => string.IsNullOrWhiteSpace(entity.Description) ? entity.Name : $"{entity.Name}\n{entity.Description}")
            .ToArray();
        var embeddings = await embeddingClient.EmbedAsync(texts, cancellationToken).ConfigureAwait(false);
        if (embeddings.Count != graph.Entities.Count)
        {
            throw new InvalidOperationException($"实体 Embedding 返回数量不匹配。Expected {graph.Entities.Count}, got {embeddings.Count}.");
        }

        for (var i = 0; i < graph.Entities.Count; i++)
        {
            graph.Entities[i].Embedding = embeddings[i].Vector;
            graph.Entities[i].EmbeddingModel = settings.Knowledge.Embedding.Model;
            graph.Entities[i].EmbeddingDimension = embeddings[i].Vector.Length;
        }
    }

    private void CleanupImportArtifacts(KnowledgeDocument document)
    {
        TryDeleteFile(document.OriginalPath);
        TryDeleteFile(document.ExtractedPath);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup after a failed import.
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
