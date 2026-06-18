namespace Athlon.Agent.Core.Knowledge;

public sealed class KnowledgeSettings
{
    public string DirectoryName { get; set; } = "knowledge-base";
    public string DatabaseFileName { get; set; } = "knowledge.db";
    public KnowledgeEmbeddingSettings Embedding { get; set; } = new();
    public KnowledgeChunkSettings Chunking { get; set; } = new();
    public KnowledgeSearchSettings Search { get; set; } = new();
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
}

public sealed class KnowledgeSearchSettings
{
    public int TopK { get; set; } = 8;
    public double MinScore { get; set; } = 0.25;
    public int MaxContentCharsPerHit { get; set; } = 1200;
}
