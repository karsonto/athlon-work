using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Microsoft.Data.Sqlite;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed partial class SqliteKnowledgeStore
{
    public async Task ReplaceDocumentGraphAsync(
        string documentId,
        DocumentGraphCommit graph,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"知识库文档不存在：{documentId}");
        document.GraphStatus = graph.Status;
        document.GraphError = graph.Error ?? "";
        document.UpdatedAt = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE knowledge_documents
                SET graph_status = $graphStatus,
                    graph_error = $graphError,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            Add(update, "$graphStatus", document.GraphStatus.ToString());
            Add(update, "$graphError", document.GraphError);
            Add(update, "$updatedAt", FormatDate(document.UpdatedAt));
            Add(update, "$id", document.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ApplyDocumentGraphAsync(connection, (SqliteTransaction)transaction, document, graph, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDocumentGraphStatusAsync(
        string documentId,
        KnowledgeGraphStatus status,
        string error,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE knowledge_documents
            SET graph_status = $graphStatus,
                graph_error = $graphError,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        Add(command, "$graphStatus", status.ToString());
        Add(command, "$graphError", error ?? "");
        Add(command, "$updatedAt", FormatDate(DateTimeOffset.UtcNow));
        Add(command, "$id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeChunk>> ListDocumentChunksAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, document_id, module_id, chunk_index, title_path, page_number, content,
                   token_count, embedding_model, embedding_dimension, embedding_blob, created_at
            FROM knowledge_chunks
            WHERE document_id = $documentId
            ORDER BY chunk_index;
            """;
        Add(command, "$documentId", documentId);
        return await ReadChunksAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<KnowledgeEntity>> ListEntitiesAsync(
        IReadOnlySet<string> moduleIds,
        CancellationToken cancellationToken = default) =>
        QueryByModulesAsync(moduleIds, static (names) => $"""
            SELECT id, module_id, name, normalized_name, entity_type, description,
                   embedding_model, embedding_dimension, embedding_blob, created_at, updated_at
            FROM knowledge_entities
            WHERE module_id IN ({string.Join(", ", names)})
            ORDER BY name COLLATE NOCASE;
            """, ReadEntityAsync, cancellationToken);

    public Task<IReadOnlyList<KnowledgeRelation>> ListRelationsAsync(
        IReadOnlySet<string> moduleIds,
        CancellationToken cancellationToken = default) =>
        QueryByModulesAsync(moduleIds, static (names) => $"""
            SELECT id, module_id, source_entity_id, target_entity_id, description, weight,
                   evidence, document_id, chunk_id
            FROM knowledge_relations
            WHERE module_id IN ({string.Join(", ", names)});
            """, ReadRelationAsync, cancellationToken);

    public Task<IReadOnlyList<KnowledgeEntityMention>> ListMentionsAsync(
        IReadOnlySet<string> moduleIds,
        CancellationToken cancellationToken = default) =>
        QueryByModulesAsync(moduleIds, static (names) => $"""
            SELECT id, entity_id, document_id, chunk_id, module_id
            FROM knowledge_entity_mentions
            WHERE module_id IN ({string.Join(", ", names)});
            """, ReadMentionAsync, cancellationToken);

    public async Task<KnowledgeModuleGraph> GetModuleGraphAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { moduleId };
        var entities = await ListEntitiesAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        var relations = await ListRelationsAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        var mentions = await ListMentionsAsync(moduleIds, cancellationToken).ConfigureAwait(false);
        var documents = await ListDocumentsAsync(moduleId, cancellationToken).ConfigureAwait(false);
        var documentNames = documents.ToDictionary(document => document.Id, document => document.FileName, StringComparer.OrdinalIgnoreCase);
        var chunks = new Dictionary<string, KnowledgeChunk>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            foreach (var chunk in await ListDocumentChunksAsync(document.Id, cancellationToken).ConfigureAwait(false))
            {
                chunks[chunk.Id] = chunk;
            }
        }

        var entityNames = entities.ToDictionary(entity => entity.Id, entity => entity.Name, StringComparer.OrdinalIgnoreCase);
        var nodes = entities
            .Select(entity =>
            {
                var evidence = mentions
                    .Where(mention => string.Equals(mention.EntityId, entity.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(mention => ToEvidence(mention, chunks, documentNames))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .GroupBy(item => item.ChunkId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Take(6)
                    .ToArray();
                return new KnowledgeGraphNode(entity.Id, entity.Name, entity.EntityType, entity.Description, evidence);
            })
            .ToArray();

        var edges = relations
            .GroupBy(relation => $"{relation.SourceEntityId}\n{relation.TargetEntityId}\n{relation.Description}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                entityNames.TryGetValue(first.SourceEntityId, out var sourceName);
                entityNames.TryGetValue(first.TargetEntityId, out var targetName);
                return new KnowledgeGraphEdge(
                    first.Id,
                    first.SourceEntityId,
                    first.TargetEntityId,
                    sourceName ?? first.SourceEntityId,
                    targetName ?? first.TargetEntityId,
                    first.Description,
                    group.Max(relation => relation.Weight),
                    group.Select(relation => relation.Evidence).FirstOrDefault(evidence => !string.IsNullOrWhiteSpace(evidence)) ?? "");
            })
            .ToArray();

        return new KnowledgeModuleGraph(moduleId, nodes, edges);
    }

    private async Task ApplyDocumentGraphAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        KnowledgeDocument document,
        DocumentGraphCommit graph,
        CancellationToken cancellationToken)
    {
        await using (var deleteMentions = connection.CreateCommand())
        {
            deleteMentions.Transaction = transaction;
            deleteMentions.CommandText = "DELETE FROM knowledge_entity_mentions WHERE document_id = $documentId;";
            Add(deleteMentions, "$documentId", document.Id);
            await deleteMentions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteRelations = connection.CreateCommand())
        {
            deleteRelations.Transaction = transaction;
            deleteRelations.CommandText = "DELETE FROM knowledge_relations WHERE document_id = $documentId;";
            Add(deleteRelations, "$documentId", document.Id);
            await deleteRelations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (graph.Status == KnowledgeGraphStatus.Indexed)
        {
            var entityIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in graph.Entities)
            {
                var entityId = await UpsertEntityAsync(connection, transaction, document.ModuleId, entity, cancellationToken)
                    .ConfigureAwait(false);
                entityIds[EntityKey(entity.EntityType, entity.NormalizedName)] = entityId;
                foreach (var chunkId in entity.ChunkIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    await using var insertMention = connection.CreateCommand();
                    insertMention.Transaction = transaction;
                    insertMention.CommandText = """
                        INSERT INTO knowledge_entity_mentions
                            (id, entity_id, document_id, chunk_id, module_id)
                        VALUES
                            ($id, $entityId, $documentId, $chunkId, $moduleId);
                        """;
                    Add(insertMention, "$id", IdGen.NewId());
                    Add(insertMention, "$entityId", entityId);
                    Add(insertMention, "$documentId", document.Id);
                    Add(insertMention, "$chunkId", chunkId);
                    Add(insertMention, "$moduleId", document.ModuleId);
                    await insertMention.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var relation in graph.Relations)
            {
                if (!entityIds.TryGetValue(EntityKey(relation.SourceType, relation.SourceNormalizedName), out var sourceId)
                    || !entityIds.TryGetValue(EntityKey(relation.TargetType, relation.TargetNormalizedName), out var targetId))
                {
                    continue;
                }

                await using var insertRelation = connection.CreateCommand();
                insertRelation.Transaction = transaction;
                insertRelation.CommandText = """
                    INSERT INTO knowledge_relations
                        (id, module_id, source_entity_id, target_entity_id, description, weight,
                         evidence, document_id, chunk_id)
                    VALUES
                        ($id, $moduleId, $sourceId, $targetId, $description, $weight,
                         $evidence, $documentId, $chunkId);
                    """;
                Add(insertRelation, "$id", IdGen.NewId());
                Add(insertRelation, "$moduleId", document.ModuleId);
                Add(insertRelation, "$sourceId", sourceId);
                Add(insertRelation, "$targetId", targetId);
                Add(insertRelation, "$description", relation.Description ?? "");
                Add(insertRelation, "$weight", Math.Clamp(relation.Weight, 1, 10));
                Add(insertRelation, "$evidence", relation.Evidence ?? "");
                Add(insertRelation, "$documentId", document.Id);
                Add(insertRelation, "$chunkId", relation.ChunkId ?? "");
                await insertRelation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await DeleteOrphanEntitiesAsync(connection, transaction, document.ModuleId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> UpsertEntityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string moduleId,
        GraphEntityDraft entity,
        CancellationToken cancellationToken)
    {
        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO knowledge_entities
                (id, module_id, name, normalized_name, entity_type, description,
                 embedding_model, embedding_dimension, embedding_blob, created_at, updated_at)
            VALUES
                ($id, $moduleId, $name, $normalizedName, $entityType, $description,
                 $embeddingModel, $embeddingDimension, $embeddingBlob, $createdAt, $updatedAt)
            ON CONFLICT(module_id, entity_type, normalized_name) DO UPDATE SET
                name = excluded.name,
                description = CASE
                    WHEN length(excluded.description) > 0 THEN excluded.description
                    ELSE knowledge_entities.description
                END,
                embedding_model = CASE
                    WHEN excluded.embedding_blob IS NOT NULL THEN excluded.embedding_model
                    ELSE knowledge_entities.embedding_model
                END,
                embedding_dimension = CASE
                    WHEN excluded.embedding_blob IS NOT NULL THEN excluded.embedding_dimension
                    ELSE knowledge_entities.embedding_dimension
                END,
                embedding_blob = COALESCE(excluded.embedding_blob, knowledge_entities.embedding_blob),
                updated_at = excluded.updated_at;
            """;
        var now = FormatDate(DateTimeOffset.UtcNow);
        Add(upsert, "$id", IdGen.NewId());
        Add(upsert, "$moduleId", moduleId);
        Add(upsert, "$name", entity.Name);
        Add(upsert, "$normalizedName", entity.NormalizedName);
        Add(upsert, "$entityType", entity.EntityType);
        Add(upsert, "$description", entity.Description ?? "");
        Add(upsert, "$embeddingModel", entity.EmbeddingModel ?? "");
        Add(upsert, "$embeddingDimension", entity.EmbeddingDimension);
        Add(upsert, "$embeddingBlob", entity.Embedding is null ? null : SerializeVector(entity.Embedding));
        Add(upsert, "$createdAt", now);
        Add(upsert, "$updatedAt", now);
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id FROM knowledge_entities
            WHERE module_id = $moduleId
              AND entity_type = $entityType
              AND normalized_name = $normalizedName;
            """;
        Add(select, "$moduleId", moduleId);
        Add(select, "$entityType", entity.EntityType);
        Add(select, "$normalizedName", entity.NormalizedName);
        var id = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return id as string ?? throw new InvalidOperationException("知识图谱实体写入后未能读回。");
    }

    private static async Task DeleteOrphanEntitiesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string moduleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM knowledge_entities
            WHERE module_id = $moduleId
              AND NOT EXISTS (
                  SELECT 1 FROM knowledge_entity_mentions m WHERE m.entity_id = knowledge_entities.id
              );
            """;
        Add(command, "$moduleId", moduleId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureDocumentGraphColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "knowledge_documents", "graph_status", "TEXT NOT NULL DEFAULT 'Pending'", cancellationToken)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "knowledge_documents", "graph_error", "TEXT NOT NULL DEFAULT ''", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add(reader.GetString(1));
            }
        }

        if (names.Contains(column))
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> QueryByModulesAsync<T>(
        IReadOnlySet<string> moduleIds,
        Func<string[], string> sql,
        Func<SqliteDataReader, T> read,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (moduleIds.Count == 0)
        {
            return Array.Empty<T>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var parameterNames = moduleIds.Select((_, index) => $"$module{index}").ToArray();
        command.CommandText = sql(parameterNames);
        var index = 0;
        foreach (var moduleId in moduleIds)
        {
            Add(command, parameterNames[index++], moduleId);
        }

        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(read(reader));
        }

        return result;
    }

    private static async Task<IReadOnlyList<KnowledgeChunk>> ReadChunksAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<KnowledgeChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadChunk(reader));
        }

        return result;
    }

    private static KnowledgeEntity ReadEntityAsync(SqliteDataReader reader)
    {
        var blobOrdinal = reader.GetOrdinal("embedding_blob");
        return new KnowledgeEntity
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            ModuleId = reader.GetString(reader.GetOrdinal("module_id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            NormalizedName = reader.GetString(reader.GetOrdinal("normalized_name")),
            EntityType = reader.GetString(reader.GetOrdinal("entity_type")),
            Description = reader.GetString(reader.GetOrdinal("description")),
            EmbeddingModel = reader.GetString(reader.GetOrdinal("embedding_model")),
            EmbeddingDimension = reader.GetInt32(reader.GetOrdinal("embedding_dimension")),
            Embedding = reader.IsDBNull(blobOrdinal) ? null : DeserializeVector((byte[])reader.GetValue(blobOrdinal)),
            CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = ParseDate(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    private static KnowledgeRelation ReadRelationAsync(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        ModuleId = reader.GetString(reader.GetOrdinal("module_id")),
        SourceEntityId = reader.GetString(reader.GetOrdinal("source_entity_id")),
        TargetEntityId = reader.GetString(reader.GetOrdinal("target_entity_id")),
        Description = reader.GetString(reader.GetOrdinal("description")),
        Weight = reader.GetInt32(reader.GetOrdinal("weight")),
        Evidence = reader.GetString(reader.GetOrdinal("evidence")),
        DocumentId = reader.GetString(reader.GetOrdinal("document_id")),
        ChunkId = reader.GetString(reader.GetOrdinal("chunk_id"))
    };

    private static KnowledgeEntityMention ReadMentionAsync(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        EntityId = reader.GetString(reader.GetOrdinal("entity_id")),
        DocumentId = reader.GetString(reader.GetOrdinal("document_id")),
        ChunkId = reader.GetString(reader.GetOrdinal("chunk_id")),
        ModuleId = reader.GetString(reader.GetOrdinal("module_id"))
    };

    private static KnowledgeGraphEvidence? ToEvidence(
        KnowledgeEntityMention mention,
        IReadOnlyDictionary<string, KnowledgeChunk> chunks,
        IReadOnlyDictionary<string, string> documentNames)
    {
        if (!chunks.TryGetValue(mention.ChunkId, out var chunk))
        {
            return null;
        }

        documentNames.TryGetValue(mention.DocumentId, out var fileName);
        var content = chunk.Content.Length <= 600 ? chunk.Content : chunk.Content[..600];
        return new KnowledgeGraphEvidence(
            mention.DocumentId,
            fileName ?? mention.DocumentId,
            chunk.Id,
            chunk.TitlePath,
            chunk.PageNumber,
            content);
    }

    private static string EntityKey(string type, string normalizedName) =>
        type.ToLowerInvariant() + "\n" + normalizedName;
}
