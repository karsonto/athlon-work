using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class KnowledgeSearchService(
    IKnowledgeStore store,
    IEmbeddingClient embeddingClient,
    ISessionKnowledgeState sessionKnowledgeState,
    AppSettings settings) : IKnowledgeSearchService
{
    public async Task<KnowledgeSearchResult> SearchAsync(
        string sessionId,
        string query,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(sessionId))
        {
            return KnowledgeSearchResult.Empty;
        }

        var selectedModules = await sessionKnowledgeState.GetModuleIdsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (selectedModules.Count == 0)
        {
            return KnowledgeSearchResult.Empty;
        }

        return await SearchInScopeAsync(query, selectedModules, documentId: null, topK, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeSearchResult> SearchInScopeAsync(
        string query,
        IReadOnlySet<string> moduleIds,
        string? documentId = null,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || moduleIds.Count == 0)
        {
            return KnowledgeSearchResult.Empty;
        }

        var queryVector = (await embeddingClient.EmbedAsync([query], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Vector;
        if (queryVector is null || queryVector.Length == 0)
        {
            return KnowledgeSearchResult.Empty;
        }

        var chunks = await store.ListSearchableChunksAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(documentId))
        {
            chunks = chunks
                .Where(chunk => string.Equals(chunk.DocumentId, documentId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (chunks.Count == 0)
        {
            return KnowledgeSearchResult.Empty;
        }

        var modules = (await store.ListModulesAsync(cancellationToken).ConfigureAwait(false))
            .Select(summary => summary.Module)
            .ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var documents = (await store.ListDocumentsAsync(null, cancellationToken).ConfigureAwait(false))
            .ToDictionary(document => document.Id, StringComparer.OrdinalIgnoreCase);
        var limit = Math.Clamp(topK ?? settings.Knowledge.Search.TopK, 1, 50);
        var minScore = settings.Knowledge.Search.MinScore;
        var maxContentChars = Math.Max(200, settings.Knowledge.Search.MaxContentCharsPerHit);
        var hits = chunks
            .Select(chunk => new { Chunk = chunk, Score = CosineSimilarity(queryVector, chunk.Embedding) })
            .Where(item => item.Score >= minScore)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.ChunkIndex)
            .ThenBy(item => item.Chunk.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(item =>
            {
                documents.TryGetValue(item.Chunk.DocumentId, out var document);
                modules.TryGetValue(item.Chunk.ModuleId, out var module);
                return new KnowledgeSearchHit(
                    item.Chunk.Id,
                    item.Chunk.DocumentId,
                    item.Chunk.ModuleId,
                    module?.Name ?? item.Chunk.ModuleId,
                    document?.FileName ?? item.Chunk.DocumentId,
                    item.Chunk.TitlePath,
                    item.Chunk.PageNumber,
                    item.Score,
                    Truncate(item.Chunk.Content, maxContentChars));
            })
            .ToArray();
        var graph = await BuildGraphContextAsync(
                query,
                queryVector,
                moduleIds,
                documentId,
                minScore,
                maxContentChars,
                chunks,
                modules,
                documents,
                cancellationToken)
            .ConfigureAwait(false);
        return new KnowledgeSearchResult(hits, graph);
    }

    private async Task<KnowledgeGraphSearchBlock> BuildGraphContextAsync(
        string query,
        float[] queryVector,
        IReadOnlySet<string> moduleIds,
        string? documentId,
        double minScore,
        int maxContentChars,
        IReadOnlyList<KnowledgeChunk> chunks,
        IReadOnlyDictionary<string, KnowledgeModule> modules,
        IReadOnlyDictionary<string, KnowledgeDocument> documents,
        CancellationToken cancellationToken)
    {
        var entities = await store.ListEntitiesAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        if (entities.Count == 0)
        {
            return KnowledgeGraphSearchBlock.Empty;
        }

        var relations = await store.ListRelationsAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        var candidates = entities;
        if (!string.IsNullOrWhiteSpace(documentId))
        {
            var mentions = await store.ListMentionsAsync(moduleIds, cancellationToken).ConfigureAwait(false);
            relations = relations
                .Where(relation => string.Equals(relation.DocumentId, documentId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mention in mentions)
            {
                if (string.Equals(mention.DocumentId, documentId, StringComparison.OrdinalIgnoreCase))
                {
                    allowed.Add(mention.EntityId);
                }
            }

            foreach (var relation in relations)
            {
                allowed.Add(relation.SourceEntityId);
                allowed.Add(relation.TargetEntityId);
            }

            candidates = entities
                .Where(entity => allowed.Contains(entity.Id))
                .ToArray();
        }

        var graphSettings = settings.Knowledge.Graph ?? new KnowledgeGraphSettings();
        var maxEntities = Math.Clamp(graphSettings.MaxEntities, 1, 50);
        var maxRelations = Math.Clamp(graphSettings.MaxRelations, 1, 50);
        var selected = SelectEntities(candidates, query, queryVector, minScore, maxEntities);
        if (selected.Count == 0)
        {
            return KnowledgeGraphSearchBlock.Empty;
        }

        var selectedIds = selected.Select(entity => entity.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityById = entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        var chunksById = chunks.ToDictionary(chunk => chunk.Id, StringComparer.OrdinalIgnoreCase);
        var graphRelations = relations
            .Where(relation => selectedIds.Contains(relation.SourceEntityId) || selectedIds.Contains(relation.TargetEntityId))
            .OrderByDescending(relation => relation.Weight)
            .ThenBy(relation => relation.Description, StringComparer.Ordinal)
            .ThenBy(relation => relation.Id, StringComparer.Ordinal)
            .Take(maxRelations)
            .Select(relation => ToGraphRelation(relation, entityById, chunksById, modules, documents, maxContentChars))
            .Where(relation => relation is not null)
            .Cast<KnowledgeGraphSearchRelation>()
            .ToArray();
        var graphEntities = selected
            .Select(entity => new KnowledgeGraphSearchEntity(entity.Name, entity.EntityType, entity.Description))
            .ToArray();
        return new KnowledgeGraphSearchBlock(graphEntities, graphRelations);
    }

    private static List<KnowledgeEntity> SelectEntities(
        IReadOnlyList<KnowledgeEntity> entities,
        string query,
        float[] queryVector,
        double minScore,
        int maxEntities)
    {
        var named = new List<(KnowledgeEntity Entity, double Score)>();
        var similar = new List<(KnowledgeEntity Entity, double Score)>();
        foreach (var entity in entities)
        {
            var score = CosineSimilarity(queryVector, entity.Embedding);
            if (QueryMentionsEntity(query, entity.Name))
            {
                named.Add((entity, score));
            }
            else if (score >= minScore)
            {
                similar.Add((entity, score));
            }
        }

        named.Sort(CompareEntityRank);
        similar.Sort(CompareEntityRank);
        var selected = new List<KnowledgeEntity>(Math.Min(maxEntities, named.Count + similar.Count));
        foreach (var item in named)
        {
            if (selected.Count >= maxEntities)
            {
                break;
            }

            selected.Add(item.Entity);
        }

        foreach (var item in similar)
        {
            if (selected.Count >= maxEntities)
            {
                break;
            }

            selected.Add(item.Entity);
        }

        return selected;
    }

    private static int CompareEntityRank(
        (KnowledgeEntity Entity, double Score) left,
        (KnowledgeEntity Entity, double Score) right)
    {
        var byScore = right.Score.CompareTo(left.Score);
        return byScore != 0
            ? byScore
            : string.Compare(left.Entity.Name, right.Entity.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static KnowledgeGraphSearchRelation? ToGraphRelation(
        KnowledgeRelation relation,
        IReadOnlyDictionary<string, KnowledgeEntity> entityById,
        IReadOnlyDictionary<string, KnowledgeChunk> chunksById,
        IReadOnlyDictionary<string, KnowledgeModule> modules,
        IReadOnlyDictionary<string, KnowledgeDocument> documents,
        int maxContentChars)
    {
        if (!entityById.TryGetValue(relation.SourceEntityId, out var source)
            || !entityById.TryGetValue(relation.TargetEntityId, out var target))
        {
            return null;
        }

        documents.TryGetValue(relation.DocumentId, out var document);
        modules.TryGetValue(relation.ModuleId, out var module);
        var fileName = document?.FileName ?? relation.DocumentId;
        var titlePath = "";
        int? pageNumber = null;
        var evidence = relation.Evidence ?? "";
        if (!string.IsNullOrWhiteSpace(relation.ChunkId) && chunksById.TryGetValue(relation.ChunkId, out var chunk))
        {
            documents.TryGetValue(chunk.DocumentId, out var chunkDocument);
            modules.TryGetValue(chunk.ModuleId, out var chunkModule);
            document ??= chunkDocument;
            module ??= chunkModule;
            fileName = chunkDocument?.FileName ?? fileName;
            titlePath = chunk.TitlePath;
            pageNumber = chunk.PageNumber;
            evidence = Truncate(chunk.Content, maxContentChars);
        }

        return new KnowledgeGraphSearchRelation(
            source.Name,
            target.Name,
            relation.Description ?? "",
            relation.Weight,
            module?.Name ?? relation.ModuleId,
            fileName,
            titlePath,
            pageNumber,
            evidence);
    }

    private static string Truncate(string content, int maxContentChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxContentChars)
        {
            return content ?? "";
        }

        return content[..maxContentChars] + "\n... (truncated)";
    }

    internal static bool QueryMentionsEntity(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        var index = query.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        if (!trimmed.All(char.IsAsciiLetterOrDigit))
        {
            return true;
        }

        var before = index == 0 || !char.IsAsciiLetterOrDigit(query[index - 1]);
        var afterIndex = index + trimmed.Length;
        var after = afterIndex >= query.Length || !char.IsAsciiLetterOrDigit(query[afterIndex]);
        return before && after;
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
