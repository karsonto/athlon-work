namespace Athlon.Agent.Core.Knowledge;

public enum KnowledgeDocumentStatus
{
    Pending,
    Extracting,
    Chunking,
    Embedding,
    Indexed,
    Failed
}

public sealed class KnowledgeModule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class KnowledgeDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ModuleId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FileType { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string ExtractedPath { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public KnowledgeDocumentStatus Status { get; set; } = KnowledgeDocumentStatus.Pending;
    public string LastError { get; set; } = "";
    public int ChunkCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class KnowledgeChunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DocumentId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public int ChunkIndex { get; set; }
    public string TitlePath { get; set; } = "";
    public int? PageNumber { get; set; }
    public string Content { get; set; } = "";
    public int TokenCount { get; set; }
    public string EmbeddingModel { get; set; } = "";
    public int EmbeddingDimension { get; set; }
    public float[]? Embedding { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record KnowledgeSearchHit(
    string ChunkId,
    string DocumentId,
    string ModuleId,
    string ModuleName,
    string FileName,
    string TitlePath,
    int? PageNumber,
    double Score,
    string Content);

public sealed record KnowledgeModuleSummary(
    KnowledgeModule Module,
    int DocumentCount,
    int ChunkCount);
