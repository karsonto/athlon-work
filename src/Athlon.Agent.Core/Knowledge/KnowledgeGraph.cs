using System.Text.Json;

namespace Athlon.Agent.Core.Knowledge;

public enum KnowledgeGraphStatus
{
    Pending,
    Indexed,
    Failed,
    Skipped
}

public sealed class KnowledgeEntity
{
    public string Id { get; set; } = IdGen.NewId();
    public string ModuleId { get; set; } = "";
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string Description { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public int EmbeddingDimension { get; set; }
    public float[]? Embedding { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class KnowledgeEntityMention
{
    public string Id { get; set; } = IdGen.NewId();
    public string EntityId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string ModuleId { get; set; } = "";
}

public sealed class KnowledgeRelation
{
    public string Id { get; set; } = IdGen.NewId();
    public string ModuleId { get; set; } = "";
    public string SourceEntityId { get; set; } = "";
    public string TargetEntityId { get; set; } = "";
    public string Description { get; set; } = "";
    public int Weight { get; set; } = 1;
    public string Evidence { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string ChunkId { get; set; } = "";
}

public sealed class GraphEntityDraft
{
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string Description { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public int EmbeddingDimension { get; set; }
    public float[]? Embedding { get; set; }
    public List<string> ChunkIds { get; set; } = [];
}

public sealed class GraphRelationDraft
{
    public string SourceNormalizedName { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string TargetNormalizedName { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string Description { get; set; } = "";
    public int Weight { get; set; } = 1;
    public string Evidence { get; set; } = "";
    public string ChunkId { get; set; } = "";
}

public sealed class DocumentGraphCommit
{
    public KnowledgeGraphStatus Status { get; set; } = KnowledgeGraphStatus.Pending;
    public string Error { get; set; } = "";
    public List<GraphEntityDraft> Entities { get; set; } = [];
    public List<GraphRelationDraft> Relations { get; set; } = [];

    public static DocumentGraphCommit Skipped() => new() { Status = KnowledgeGraphStatus.Skipped };

    public static DocumentGraphCommit Failed(string error) => new()
    {
        Status = KnowledgeGraphStatus.Failed,
        Error = error ?? ""
    };
}

public sealed record KnowledgeGraphExtraction(
    IReadOnlyList<ExtractedKnowledgeEntity> Entities,
    IReadOnlyList<ExtractedKnowledgeRelation> Relations);

public sealed record ExtractedKnowledgeEntity(
    string ChunkId,
    string Name,
    string NormalizedName,
    string Type,
    string Description);

public sealed record ExtractedKnowledgeRelation(
    string ChunkId,
    string SourceName,
    string SourceNormalizedName,
    string SourceType,
    string TargetName,
    string TargetNormalizedName,
    string TargetType,
    string Description,
    int Weight,
    string Evidence);

public sealed record KnowledgeGraphRebuildResult(int Succeeded, int Failed);

public sealed record KnowledgeGraphEvidence(
    string DocumentId,
    string FileName,
    string ChunkId,
    string TitlePath,
    int? PageNumber,
    string Content);

public sealed record KnowledgeGraphNode(
    string Id,
    string Name,
    string EntityType,
    string Description,
    IReadOnlyList<KnowledgeGraphEvidence> Evidence);

public sealed record KnowledgeGraphEdge(
    string Id,
    string SourceId,
    string TargetId,
    string SourceName,
    string TargetName,
    string Description,
    int Weight,
    string Evidence);

public sealed record KnowledgeModuleGraph(
    string ModuleId,
    IReadOnlyList<KnowledgeGraphNode> Nodes,
    IReadOnlyList<KnowledgeGraphEdge> Edges);

public static class KnowledgeEntityNames
{
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var parts = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).ToLowerInvariant();
    }
}

public static class KnowledgeGraphExtractionParser
{
    public static KnowledgeGraphExtraction Parse(
        string content,
        IReadOnlyCollection<string> allowedTypes,
        IReadOnlySet<string>? chunkIds = null)
    {
        var raw = ParseRaw(content, allowedTypes, chunkIds);
        return Link(raw.Entities, raw.Relations);
    }

    public static KnowledgeGraphExtraction ParseRaw(
        string content,
        IReadOnlyCollection<string> allowedTypes,
        IReadOnlySet<string>? chunkIds = null)
    {
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in allowedTypes)
        {
            var canonical = type.Trim().ToLowerInvariant();
            if (canonical.Length > 0)
            {
                allowed[canonical] = canonical;
            }
        }

        using var json = JsonDocument.Parse(ExtractJsonObject(content));
        var root = json.RootElement;
        var entities = new List<ExtractedKnowledgeEntity>();
        var relations = new List<ExtractedKnowledgeRelation>();
        if (root.TryGetProperty("entities", out var entityArray) && entityArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in entityArray.EnumerateArray())
            {
                if (TryReadEntity(item, allowed, chunkIds, out var entity))
                {
                    entities.Add(entity);
                }
            }
        }

        if (root.TryGetProperty("relations", out var relationArray) && relationArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in relationArray.EnumerateArray())
            {
                if (TryReadRelation(item, allowed, chunkIds, out var relation))
                {
                    relations.Add(relation);
                }
            }
        }

        return new KnowledgeGraphExtraction(entities, relations);
    }

    public static KnowledgeGraphExtraction Link(
        IReadOnlyList<ExtractedKnowledgeEntity> entities,
        IReadOnlyList<ExtractedKnowledgeRelation> relations)
    {
        var byKey = new Dictionary<string, ExtractedKnowledgeEntity>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<ExtractedKnowledgeEntity>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            byKey[Key(entity.Type, entity.NormalizedName)] = entity;
            if (!byName.TryGetValue(entity.NormalizedName, out var list))
            {
                list = [];
                byName[entity.NormalizedName] = list;
            }

            list.Add(entity);
        }

        var linked = new List<ExtractedKnowledgeRelation>();
        foreach (var relation in relations)
        {
            if (!TryResolve(relation.SourceNormalizedName, relation.SourceType, byKey, byName, out var sourceType)
                || !TryResolve(relation.TargetNormalizedName, relation.TargetType, byKey, byName, out var targetType))
            {
                continue;
            }

            linked.Add(relation with { SourceType = sourceType, TargetType = targetType });
        }

        return new KnowledgeGraphExtraction(entities, linked);
    }

    private static bool TryResolve(
        string normalizedName,
        string type,
        Dictionary<string, ExtractedKnowledgeEntity> byKey,
        Dictionary<string, List<ExtractedKnowledgeEntity>> byName,
        out string resolvedType)
    {
        resolvedType = "";
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(type) && byKey.ContainsKey(Key(type, normalizedName)))
        {
            resolvedType = type;
            return true;
        }

        if (byName.TryGetValue(normalizedName, out var matches) && matches.Count == 1)
        {
            resolvedType = matches[0].Type;
            return true;
        }

        return false;
    }

    private static bool TryReadEntity(
        JsonElement item,
        Dictionary<string, string> allowed,
        IReadOnlySet<string>? chunkIds,
        out ExtractedKnowledgeEntity entity)
    {
        entity = null!;
        var name = ReadString(item, "name");
        var normalized = KnowledgeEntityNames.Normalize(name);
        var type = ReadString(item, "type").ToLowerInvariant();
        var chunkId = ResolveChunkId(ReadString(item, "chunkId", "chunk_id"), chunkIds);
        if (normalized.Length == 0 || chunkId.Length == 0 || !allowed.TryGetValue(type, out var canonical))
        {
            return false;
        }

        entity = new ExtractedKnowledgeEntity(
            chunkId,
            Trim(name, 200),
            normalized,
            canonical,
            Trim(ReadString(item, "description"), 1000));
        return true;
    }

    private static bool TryReadRelation(
        JsonElement item,
        Dictionary<string, string> allowed,
        IReadOnlySet<string>? chunkIds,
        out ExtractedKnowledgeRelation relation)
    {
        relation = null!;
        var source = ReadString(item, "source", "sourceName");
        var target = ReadString(item, "target", "targetName");
        var sourceNormalized = KnowledgeEntityNames.Normalize(source);
        var targetNormalized = KnowledgeEntityNames.Normalize(target);
        var chunkId = ResolveChunkId(ReadString(item, "chunkId", "chunk_id"), chunkIds);
        if (sourceNormalized.Length == 0 || targetNormalized.Length == 0 || chunkId.Length == 0)
        {
            return false;
        }

        var sourceType = ReadString(item, "sourceType", "source_type").ToLowerInvariant();
        var targetType = ReadString(item, "targetType", "target_type").ToLowerInvariant();
        if (sourceType.Length > 0 && !allowed.ContainsKey(sourceType))
        {
            return false;
        }

        if (targetType.Length > 0 && !allowed.ContainsKey(targetType))
        {
            return false;
        }

        if (sourceType.Length > 0)
        {
            sourceType = allowed[sourceType];
        }

        if (targetType.Length > 0)
        {
            targetType = allowed[targetType];
        }

        var weight = 5;
        if (item.TryGetProperty("weight", out var weightElement) && weightElement.TryGetInt32(out var parsedWeight))
        {
            weight = parsedWeight;
        }

        relation = new ExtractedKnowledgeRelation(
            chunkId,
            Trim(source, 200),
            sourceNormalized,
            sourceType,
            Trim(target, 200),
            targetNormalized,
            targetType,
            Trim(ReadString(item, "description"), 500),
            Math.Clamp(weight, 1, 10),
            Trim(ReadString(item, "evidence"), 500));
        return true;
    }

    private static string ResolveChunkId(string chunkId, IReadOnlySet<string>? chunkIds)
    {
        if (!string.IsNullOrWhiteSpace(chunkId))
        {
            if (chunkIds is null || chunkIds.Contains(chunkId))
            {
                return chunkId;
            }

            return "";
        }

        if (chunkIds is { Count: 1 })
        {
            return chunkIds.First();
        }

        return "";
    }

    internal static string ExtractJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("图谱抽取结果为空。");
        }

        var start = content.IndexOf('{');
        if (start < 0)
        {
            throw new InvalidOperationException("图谱抽取结果不是 JSON。");
        }

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < content.Length; i++)
        {
            var current = content[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (current == '\\')
                {
                    escape = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return content[start..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException("图谱抽取 JSON 不完整。");
    }

    private static string Key(string type, string normalizedName) => type + "\n" + normalizedName;

    private static string ReadString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }

        return "";
    }

    private static string Trim(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

public static class KnowledgeGraphMerger
{
    public static DocumentGraphCommit Build(KnowledgeGraphExtraction extraction)
    {
        var entities = new Dictionary<string, GraphEntityDraft>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in extraction.Entities)
        {
            var key = entity.Type + "\n" + entity.NormalizedName;
            if (!entities.TryGetValue(key, out var draft))
            {
                draft = new GraphEntityDraft
                {
                    Name = entity.Name,
                    NormalizedName = entity.NormalizedName,
                    EntityType = entity.Type,
                    Description = entity.Description
                };
                entities[key] = draft;
            }
            else if (entity.Description.Length > draft.Description.Length)
            {
                draft.Description = entity.Description;
                draft.Name = entity.Name;
            }

            if (!string.IsNullOrWhiteSpace(entity.ChunkId)
                && !draft.ChunkIds.Contains(entity.ChunkId, StringComparer.OrdinalIgnoreCase))
            {
                draft.ChunkIds.Add(entity.ChunkId);
            }
        }

        var relations = new List<GraphRelationDraft>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relation in extraction.Relations)
        {
            var sourceKey = relation.SourceType + "\n" + relation.SourceNormalizedName;
            var targetKey = relation.TargetType + "\n" + relation.TargetNormalizedName;
            if (!entities.ContainsKey(sourceKey) || !entities.ContainsKey(targetKey))
            {
                continue;
            }

            var dedupe = string.Join(
                "\n",
                sourceKey,
                targetKey,
                relation.Description,
                relation.ChunkId);
            if (!seen.Add(dedupe))
            {
                continue;
            }

            relations.Add(new GraphRelationDraft
            {
                SourceNormalizedName = relation.SourceNormalizedName,
                SourceType = relation.SourceType,
                TargetNormalizedName = relation.TargetNormalizedName,
                TargetType = relation.TargetType,
                Description = relation.Description,
                Weight = Math.Clamp(relation.Weight, 1, 10),
                Evidence = relation.Evidence,
                ChunkId = relation.ChunkId
            });
        }

        return new DocumentGraphCommit
        {
            Status = KnowledgeGraphStatus.Indexed,
            Entities = entities.Values.ToList(),
            Relations = relations
        };
    }
}
