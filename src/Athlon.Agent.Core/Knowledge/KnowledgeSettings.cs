namespace Athlon.Agent.Core.Knowledge;

public sealed class KnowledgeSettings
{
    public string DirectoryName { get; set; } = "knowledge-base";
    public string DatabaseFileName { get; set; } = "knowledge.db";
    public KnowledgeEmbeddingSettings Embedding { get; set; } = new();
    public KnowledgeChunkSettings Chunking { get; set; } = new();
    public KnowledgeSearchSettings Search { get; set; } = new();
    public KnowledgeOcrSettings Ocr { get; set; } = new();
    public KnowledgeGraphSettings Graph { get; set; } = new();
}

public sealed class KnowledgeOcrSettings
{
    /// <summary>When false, PDF import uses PdfPig text only (legacy behavior).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Deprecated: ignored. OCR runs for embedded page images (and full-page fallback when a page has no text and no images), not based on text length.
    /// </summary>
    public int MinCharsPerPage { get; set; } = 40;

    /// <summary>Max page images included in a single vision OCR request.</summary>
    public int BatchSize { get; set; } = 3;

    /// <summary>Optional DPI override for page rasterization; 0 means auto (chat-preview style).</summary>
    public int RenderDpi { get; set; }
}

public sealed class KnowledgeEmbeddingSettings
{
    public const string ApiKeySecretName = "knowledge-embedding-api-key";

    public string Provider { get; set; } = "OpenAI-Compatible";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "text-embedding-3-small";
    public int Dimension { get; set; } = 1536;
    public int BatchSize { get; set; } = 16;
}

public sealed class KnowledgeChunkSettings
{
    public int TargetChars { get; set; } = 4000;
    public int OverlapChars { get; set; } = 600;
    public int MaxChars { get; set; } = 6000;

    /// <summary>
    /// When true, text with <c># Page N</c> markers is split per page (PageNumber set).
    /// Oversized pages still use TargetChars/Overlap windows within that page.
    /// </summary>
    public bool SplitByPage { get; set; } = true;
}

public sealed class KnowledgeSearchSettings
{
    public int TopK { get; set; } = 8;
    public double MinScore { get; set; } = 0.25;
    public int MaxContentCharsPerHit { get; set; } = 1200;
}

public sealed class KnowledgeGraphSettings
{
    public static readonly string[] DefaultEntityTypes = ["organization", "person", "geo", "event"];

    /// <summary>When true, import and reindex extract entities and relations after chunk embeddings.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>GraphRAG-style entity types. The extractor keeps only these labels.</summary>
    public List<string> EntityTypes { get; set; } = [.. DefaultEntityTypes];

    /// <summary>How many chunks are sent in one extraction request.</summary>
    public int BatchSize { get; set; } = 4;

    /// <summary>How many entities a search may place in the graph context.</summary>
    public int MaxEntities { get; set; } = 8;

    /// <summary>How many direct relations a search may place in the graph context.</summary>
    public int MaxRelations { get; set; } = 10;

    public IReadOnlyList<string> NormalizedEntityTypes()
    {
        var types = (EntityTypes ?? [])
            .Select(type => type.Trim().ToLowerInvariant())
            .Where(type => type.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return types.Length == 0 ? DefaultEntityTypes : types;
    }

    public static IReadOnlyList<string> ParseEntityTypes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultEntityTypes;
        }

        var types = text
            .Split([',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(type => type.ToLowerInvariant())
            .Where(type => type.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return types.Length == 0 ? DefaultEntityTypes : types;
    }
}
