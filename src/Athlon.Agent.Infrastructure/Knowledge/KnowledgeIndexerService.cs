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
        CancellationToken cancellationToken = default)
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
        var safeFileName = $"{documentId}-{FileNameSanitizer.Sanitize(fileName)}";
        var originalsDir = Path.Combine(KnowledgeRoot, "originals", moduleId);
        Directory.CreateDirectory(originalsDir);
        var originalPath = Path.Combine(originalsDir, safeFileName);
        File.Copy(sourcePath, originalPath, overwrite: true);

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
        await ReindexDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false);
        return await store.GetDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false) ?? document;
    }

    public async Task ReindexDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        var document = await store.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"知识库文档不存在：{documentId}");

        try
        {
            document.Status = KnowledgeDocumentStatus.Extracting;
            document.LastError = "";
            await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);

            var extracted = await extractor.ExtractAsync(document.OriginalPath, cancellationToken).ConfigureAwait(false);
            var extractedDir = Path.Combine(KnowledgeRoot, "extracted", document.ModuleId);
            Directory.CreateDirectory(extractedDir);
            document.ExtractedPath = Path.Combine(extractedDir, $"{document.Id}.md");
            await AtomicFile.WriteAllTextAsync(document.ExtractedPath, extracted.Text, cancellationToken).ConfigureAwait(false);

            document.Status = KnowledgeDocumentStatus.Chunking;
            await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            var chunks = chunker.Chunk(document.Id, document.ModuleId, extracted.Text, extracted.Title).ToList();

            document.Status = KnowledgeDocumentStatus.Embedding;
            document.ChunkCount = chunks.Count;
            await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);

            var embeddings = await embeddingClient
                .EmbedAsync(chunks.Select(chunk => chunk.Content).ToArray(), cancellationToken)
                .ConfigureAwait(false);

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

            await store.ReplaceChunksAsync(document.Id, chunks, cancellationToken).ConfigureAwait(false);
            document.Status = KnowledgeDocumentStatus.Indexed;
            document.ChunkCount = chunks.Count;
            document.LastError = "";
            await store.SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
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
}
