namespace Athlon.Agent.Core.Knowledge;

public sealed record EmbeddingVector(string Text, float[] Vector);

public sealed record KnowledgeIndexingProgress(
    string Stage,
    string Message,
    int Processed,
    int Total,
    double Percent);

public interface IEmbeddingClient
{
    Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

public interface IKnowledgeStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeModuleSummary>> ListModulesAsync(CancellationToken cancellationToken = default);
    Task<KnowledgeModule> SaveModuleAsync(KnowledgeModule module, CancellationToken cancellationToken = default);
    Task DeleteModuleAsync(string moduleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string? moduleId = null, CancellationToken cancellationToken = default);
    Task<KnowledgeDocument?> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default);
    Task<KnowledgeDocument> SaveDocumentAsync(KnowledgeDocument document, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);
    Task ReplaceChunksAsync(string documentId, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeChunk>> ListSearchableChunksAsync(IReadOnlySet<string> moduleIds, CancellationToken cancellationToken = default);
}

public interface IKnowledgeIndexer
{
    Task<KnowledgeDocument> ImportDocumentAsync(
        string moduleId,
        string sourcePath,
        CancellationToken cancellationToken = default,
        IProgress<KnowledgeIndexingProgress>? progress = null);

    Task ReindexDocumentAsync(
        string documentId,
        CancellationToken cancellationToken = default,
        IProgress<KnowledgeIndexingProgress>? progress = null);
}

public interface IKnowledgeSearchService
{
    Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(
        string sessionId,
        string query,
        int? topK = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeSearchHit>> SearchInScopeAsync(
        string query,
        IReadOnlySet<string> moduleIds,
        string? documentId = null,
        int? topK = null,
        CancellationToken cancellationToken = default);
}

public interface IGlobalKnowledgeTool
{
}
