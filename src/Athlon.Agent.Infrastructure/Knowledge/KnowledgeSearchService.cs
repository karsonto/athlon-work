using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class KnowledgeSearchService(
    IKnowledgeStore store,
    IEmbeddingClient embeddingClient,
    AppSettings settings) : IKnowledgeSearchService
{
    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(
        string sessionId,
        string query,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<KnowledgeSearchHit>();
        }

        var selectedModules = await store.GetSessionSelectionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (selectedModules.Count == 0)
        {
            return Array.Empty<KnowledgeSearchHit>();
        }

        var queryVector = (await embeddingClient.EmbedAsync([query], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Vector;
        if (queryVector is null || queryVector.Length == 0)
        {
            return Array.Empty<KnowledgeSearchHit>();
        }

        var chunks = await store.ListSearchableChunksAsync(selectedModules, cancellationToken).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            return Array.Empty<KnowledgeSearchHit>();
        }

        var modules = (await store.ListModulesAsync(cancellationToken).ConfigureAwait(false))
            .Select(summary => summary.Module)
            .ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var documents = (await store.ListDocumentsAsync(null, cancellationToken).ConfigureAwait(false))
            .ToDictionary(document => document.Id, StringComparer.OrdinalIgnoreCase);
        var limit = Math.Clamp(topK ?? settings.Knowledge.Search.TopK, 1, 50);
        var minScore = settings.Knowledge.Search.MinScore;
        var maxContentChars = Math.Max(200, settings.Knowledge.Search.MaxContentCharsPerHit);

        return chunks
            .Select(chunk => new { Chunk = chunk, Score = CosineSimilarity(queryVector, chunk.Embedding) })
            .Where(item => item.Score >= minScore)
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item =>
            {
                documents.TryGetValue(item.Chunk.DocumentId, out var document);
                modules.TryGetValue(item.Chunk.ModuleId, out var module);
                var content = item.Chunk.Content.Length <= maxContentChars
                    ? item.Chunk.Content
                    : item.Chunk.Content[..maxContentChars] + "\n... (truncated)";
                return new KnowledgeSearchHit(
                    item.Chunk.Id,
                    item.Chunk.DocumentId,
                    item.Chunk.ModuleId,
                    module?.Name ?? item.Chunk.ModuleId,
                    document?.FileName ?? item.Chunk.DocumentId,
                    item.Chunk.TitlePath,
                    item.Chunk.PageNumber,
                    item.Score,
                    content);
            })
            .ToArray();
    }

    internal static double CosineSimilarity(float[] query, float[]? candidate)
    {
        if (candidate is null || query.Length == 0 || candidate.Length != query.Length)
        {
            return 0;
        }

        double dot = 0;
        double queryNorm = 0;
        double candidateNorm = 0;
        for (var i = 0; i < query.Length; i++)
        {
            dot += query[i] * candidate[i];
            queryNorm += query[i] * query[i];
            candidateNorm += candidate[i] * candidate[i];
        }

        if (queryNorm <= 0 || candidateNorm <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(queryNorm) * Math.Sqrt(candidateNorm));
    }
}
