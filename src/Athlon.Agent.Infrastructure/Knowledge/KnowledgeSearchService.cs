using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class KnowledgeSearchService(
    IKnowledgeStore store,
    IEmbeddingClient embeddingClient,
    ISessionKnowledgeState sessionKnowledgeState,
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

        var selectedModules = await sessionKnowledgeState.GetModuleIdsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (selectedModules.Count == 0)
        {
            return Array.Empty<KnowledgeSearchHit>();
        }

        return await SearchInScopeAsync(query, selectedModules, documentId: null, topK, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchInScopeAsync(
        string query,
        IReadOnlySet<string> moduleIds,
        string? documentId = null,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || moduleIds.Count == 0)
        {
            return Array.Empty<KnowledgeSearchHit>();
        }

        var queryVector = (await embeddingClient.EmbedAsync([query], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Vector;
        if (queryVector is null || queryVector.Length == 0)
        {
            return Array.Empty<KnowledgeSearchHit>();
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
        var vectorScores = chunks.ToDictionary(
            chunk => chunk.Id,
            chunk => CosineSimilarity(queryVector, chunk.Embedding),
            StringComparer.OrdinalIgnoreCase);
        var graphScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var relationLines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await ApplyGraphScoresAsync(query, queryVector, moduleIds, minScore, vectorScores, graphScores, relationLines, cancellationToken)
            .ConfigureAwait(false);

        return chunks
            .Select(chunk =>
            {
                var vectorScore = vectorScores[chunk.Id];
                graphScores.TryGetValue(chunk.Id, out var graphScore);
                var vectorHit = vectorScore >= minScore;
                var score = vectorHit ? Math.Max(vectorScore, graphScore) : graphScore;
                return new { Chunk = chunk, Score = score, Keep = vectorHit || graphScore >= minScore };
            })
            .Where(item => item.Keep)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.ChunkIndex)
            .ThenBy(item => item.Chunk.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(item =>
            {
                documents.TryGetValue(item.Chunk.DocumentId, out var document);
                modules.TryGetValue(item.Chunk.ModuleId, out var module);
                var content = item.Chunk.Content.Length <= maxContentChars
                    ? item.Chunk.Content
                    : item.Chunk.Content[..maxContentChars] + "\n... (truncated)";
                relationLines.TryGetValue(item.Chunk.Id, out var lines);
                return new KnowledgeSearchHit(
                    item.Chunk.Id,
                    item.Chunk.DocumentId,
                    item.Chunk.ModuleId,
                    module?.Name ?? item.Chunk.ModuleId,
                    document?.FileName ?? item.Chunk.DocumentId,
                    item.Chunk.TitlePath,
                    item.Chunk.PageNumber,
                    item.Score,
                    content,
                    lines is null ? "" : string.Join("\n", lines));
            })
            .ToArray();
    }

    private async Task ApplyGraphScoresAsync(
        string query,
        float[] queryVector,
        IReadOnlySet<string> moduleIds,
        double minScore,
        Dictionary<string, double> vectorScores,
        Dictionary<string, double> graphScores,
        Dictionary<string, List<string>> relationLines,
        CancellationToken cancellationToken)
    {
        var entities = await store.ListEntitiesAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        if (entities.Count == 0)
        {
            return;
        }

        var seeds = new List<(KnowledgeEntity Entity, double Score)>();
        foreach (var entity in entities)
        {
            var score = CosineSimilarity(queryVector, entity.Embedding);
            if (QueryMentionsEntity(query, entity.Name))
            {
                score = Math.Max(score, 1);
            }

            if (score >= minScore)
            {
                seeds.Add((entity, score));
            }
        }

        if (seeds.Count == 0)
        {
            return;
        }

        seeds = seeds
            .OrderByDescending(seed => seed.Score)
            .Take(MaxSeedEntities)
            .ToList();
        var seedIds = seeds.Select(seed => seed.Entity.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seedScores = seeds.ToDictionary(seed => seed.Entity.Id, seed => seed.Score, StringComparer.OrdinalIgnoreCase);
        var entityNames = entities.ToDictionary(entity => entity.Id, entity => entity.Name, StringComparer.OrdinalIgnoreCase);
        var relations = await store.ListRelationsAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        var mentions = await store.ListMentionsAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        var chunksByEntity = mentions
            .GroupBy(mention => mention.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(mention => mention.ChunkId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        void Boost(string? chunkId, double score)
        {
            if (string.IsNullOrWhiteSpace(chunkId) || !vectorScores.ContainsKey(chunkId))
            {
                return;
            }

            var decayed = score * GraphHopDecay;
            if (!graphScores.TryGetValue(chunkId, out var existing) || decayed > existing)
            {
                graphScores[chunkId] = decayed;
            }
        }

        foreach (var seed in seeds)
        {
            if (chunksByEntity.TryGetValue(seed.Entity.Id, out var ownChunks))
            {
                foreach (var chunkId in ownChunks)
                {
                    Boost(chunkId, seed.Score);
                }
            }
        }

        foreach (var relation in relations)
        {
            var sourceIsSeed = seedIds.Contains(relation.SourceEntityId);
            var targetIsSeed = seedIds.Contains(relation.TargetEntityId);
            if (!sourceIsSeed && !targetIsSeed)
            {
                continue;
            }

            var score = 0d;
            if (sourceIsSeed)
            {
                score = Math.Max(score, seedScores[relation.SourceEntityId]);
            }

            if (targetIsSeed)
            {
                score = Math.Max(score, seedScores[relation.TargetEntityId]);
            }

            Boost(relation.ChunkId, score);
            if (sourceIsSeed && chunksByEntity.TryGetValue(relation.TargetEntityId, out var targetChunks))
            {
                foreach (var chunkId in targetChunks)
                {
                    Boost(chunkId, score);
                }
            }

            if (targetIsSeed && chunksByEntity.TryGetValue(relation.SourceEntityId, out var sourceChunks))
            {
                foreach (var chunkId in sourceChunks)
                {
                    Boost(chunkId, score);
                }
            }

            if (!entityNames.TryGetValue(relation.SourceEntityId, out var sourceName)
                || !entityNames.TryGetValue(relation.TargetEntityId, out var targetName))
            {
                continue;
            }

            var predicate = string.IsNullOrWhiteSpace(relation.Description) ? "相关" : relation.Description.Trim();
            var line = $"{sourceName} —{predicate}→ {targetName}";
            void AddLine(string? chunkId)
            {
                if (string.IsNullOrWhiteSpace(chunkId))
                {
                    return;
                }

                if (!relationLines.TryGetValue(chunkId, out var lines))
                {
                    lines = [];
                    relationLines[chunkId] = lines;
                }

                if (lines.Count < 4 && !lines.Contains(line, StringComparer.Ordinal))
                {
                    lines.Add(line);
                }
            }

            AddLine(relation.ChunkId);
            if (chunksByEntity.TryGetValue(relation.SourceEntityId, out var mentionedBySource))
            {
                foreach (var chunkId in mentionedBySource)
                {
                    AddLine(chunkId);
                }
            }

            if (chunksByEntity.TryGetValue(relation.TargetEntityId, out var mentionedByTarget))
            {
                foreach (var chunkId in mentionedByTarget)
                {
                    AddLine(chunkId);
                }
            }
        }
    }

    private const int MaxSeedEntities = 8;
    private const double GraphHopDecay = 0.85;

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
