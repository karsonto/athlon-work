using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Microsoft.Data.Sqlite;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class SqliteKnowledgeStore(IAppPathProvider paths, AppSettings settings) : IKnowledgeStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string KnowledgeRoot => Path.Combine(paths.RootPath, settings.Knowledge.DirectoryName);
    private string DatabasePath => Path.Combine(KnowledgeRoot, settings.Knowledge.DatabaseFileName);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(KnowledgeRoot);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, """
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS knowledge_modules (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS knowledge_documents (
                    id TEXT PRIMARY KEY,
                    module_id TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    file_type TEXT NOT NULL,
                    original_path TEXT NOT NULL,
                    extracted_path TEXT NOT NULL DEFAULT '',
                    content_hash TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL,
                    last_error TEXT NOT NULL DEFAULT '',
                    chunk_count INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY (module_id) REFERENCES knowledge_modules(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_knowledge_documents_module
                    ON knowledge_documents(module_id);

                CREATE TABLE IF NOT EXISTS knowledge_chunks (
                    id TEXT PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    module_id TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    title_path TEXT NOT NULL DEFAULT '',
                    page_number INTEGER NULL,
                    content TEXT NOT NULL,
                    token_count INTEGER NOT NULL DEFAULT 0,
                    embedding_model TEXT NOT NULL DEFAULT '',
                    embedding_dimension INTEGER NOT NULL DEFAULT 0,
                    embedding_blob BLOB NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (document_id) REFERENCES knowledge_documents(id) ON DELETE CASCADE,
                    FOREIGN KEY (module_id) REFERENCES knowledge_modules(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_knowledge_chunks_module
                    ON knowledge_chunks(module_id);
                CREATE INDEX IF NOT EXISTS idx_knowledge_chunks_document
                    ON knowledge_chunks(document_id);
                """, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeModuleSummary>> ListModulesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.name, m.description, m.created_at, m.updated_at,
                   (SELECT COUNT(*) FROM knowledge_documents d WHERE d.module_id = m.id) AS document_count,
                   (SELECT COUNT(*) FROM knowledge_chunks c WHERE c.module_id = m.id) AS chunk_count
            FROM knowledge_modules m
            ORDER BY m.name COLLATE NOCASE;
            """;

        var result = new List<KnowledgeModuleSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var module = new KnowledgeModule
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.GetString(2),
                CreatedAt = ParseDate(reader.GetString(3)),
                UpdatedAt = ParseDate(reader.GetString(4))
            };
            result.Add(new KnowledgeModuleSummary(module, reader.GetInt32(5), reader.GetInt32(6)));
        }

        return result;
    }

    public async Task<KnowledgeModule> SaveModuleAsync(KnowledgeModule module, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        module.Id = string.IsNullOrWhiteSpace(module.Id) ? Guid.NewGuid().ToString("N") : module.Id;
        module.CreatedAt = module.CreatedAt == default ? DateTimeOffset.UtcNow : module.CreatedAt;
        module.UpdatedAt = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO knowledge_modules (id, name, description, created_at, updated_at)
            VALUES ($id, $name, $description, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                updated_at = excluded.updated_at;
            """;
        Add(command, "$id", module.Id);
        Add(command, "$name", module.Name);
        Add(command, "$description", module.Description);
        Add(command, "$createdAt", FormatDate(module.CreatedAt));
        Add(command, "$updatedAt", FormatDate(module.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return module;
    }

    public async Task DeleteModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM knowledge_modules WHERE id = $id;";
        Add(command, "$id", moduleId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string? moduleId = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(moduleId)
            ? "SELECT * FROM knowledge_documents ORDER BY updated_at DESC;"
            : "SELECT * FROM knowledge_documents WHERE module_id = $moduleId ORDER BY updated_at DESC;";
        if (!string.IsNullOrWhiteSpace(moduleId))
        {
            Add(command, "$moduleId", moduleId);
        }

        var result = new List<KnowledgeDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadDocument(reader));
        }

        return result;
    }

    public async Task<KnowledgeDocument?> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM knowledge_documents WHERE id = $id;";
        Add(command, "$id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDocument(reader) : null;
    }

    public async Task<KnowledgeDocument> SaveDocumentAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        document.Id = string.IsNullOrWhiteSpace(document.Id) ? Guid.NewGuid().ToString("N") : document.Id;
        document.CreatedAt = document.CreatedAt == default ? DateTimeOffset.UtcNow : document.CreatedAt;
        document.UpdatedAt = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO knowledge_documents
                (id, module_id, file_name, file_type, original_path, extracted_path, content_hash,
                 status, last_error, chunk_count, created_at, updated_at)
            VALUES
                ($id, $moduleId, $fileName, $fileType, $originalPath, $extractedPath, $contentHash,
                 $status, $lastError, $chunkCount, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                module_id = excluded.module_id,
                file_name = excluded.file_name,
                file_type = excluded.file_type,
                original_path = excluded.original_path,
                extracted_path = excluded.extracted_path,
                content_hash = excluded.content_hash,
                status = excluded.status,
                last_error = excluded.last_error,
                chunk_count = excluded.chunk_count,
                updated_at = excluded.updated_at;
            """;
        AddDocumentParameters(command, document);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM knowledge_documents WHERE id = $id;";
        Add(command, "$id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceChunksAsync(string documentId, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM knowledge_chunks WHERE document_id = $documentId;";
            Add(delete, "$documentId", documentId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in chunks)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO knowledge_chunks
                    (id, document_id, module_id, chunk_index, title_path, page_number, content,
                     token_count, embedding_model, embedding_dimension, embedding_blob, created_at)
                VALUES
                    ($id, $documentId, $moduleId, $chunkIndex, $titlePath, $pageNumber, $content,
                     $tokenCount, $embeddingModel, $embeddingDimension, $embeddingBlob, $createdAt);
                """;
            Add(insert, "$id", string.IsNullOrWhiteSpace(chunk.Id) ? Guid.NewGuid().ToString("N") : chunk.Id);
            Add(insert, "$documentId", documentId);
            Add(insert, "$moduleId", chunk.ModuleId);
            Add(insert, "$chunkIndex", chunk.ChunkIndex);
            Add(insert, "$titlePath", chunk.TitlePath);
            Add(insert, "$pageNumber", chunk.PageNumber);
            Add(insert, "$content", chunk.Content);
            Add(insert, "$tokenCount", chunk.TokenCount);
            Add(insert, "$embeddingModel", chunk.EmbeddingModel);
            Add(insert, "$embeddingDimension", chunk.EmbeddingDimension);
            Add(insert, "$embeddingBlob", chunk.Embedding is null ? null : SerializeVector(chunk.Embedding));
            Add(insert, "$createdAt", FormatDate(chunk.CreatedAt == default ? DateTimeOffset.UtcNow : chunk.CreatedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeChunk>> ListSearchableChunksAsync(IReadOnlySet<string> moduleIds, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (moduleIds.Count == 0)
        {
            return Array.Empty<KnowledgeChunk>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var parameterNames = moduleIds.Select((_, index) => $"$module{index}").ToArray();
        command.CommandText = $"""
            SELECT id, document_id, module_id, chunk_index, title_path, page_number, content,
                   token_count, embedding_model, embedding_dimension, embedding_blob, created_at
            FROM knowledge_chunks
            WHERE embedding_blob IS NOT NULL
              AND module_id IN ({string.Join(", ", parameterNames)})
            ORDER BY module_id, document_id, chunk_index;
            """;
        var i = 0;
        foreach (var moduleId in moduleIds)
        {
            Add(command, parameterNames[i++], moduleId);
        }

        var result = new List<KnowledgeChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadChunk(reader));
        }

        return result;
    }

    public string GetOriginalsDirectory(string moduleId)
    {
        var path = Path.Combine(KnowledgeRoot, "originals", moduleId);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetExtractedDirectory(string moduleId)
    {
        var path = Path.Combine(KnowledgeRoot, "extracted", moduleId);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static float[] DeserializeVector(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            return Array.Empty<float>();
        }

        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void AddDocumentParameters(SqliteCommand command, KnowledgeDocument document)
    {
        Add(command, "$id", document.Id);
        Add(command, "$moduleId", document.ModuleId);
        Add(command, "$fileName", document.FileName);
        Add(command, "$fileType", document.FileType);
        Add(command, "$originalPath", document.OriginalPath);
        Add(command, "$extractedPath", document.ExtractedPath);
        Add(command, "$contentHash", document.ContentHash);
        Add(command, "$status", document.Status.ToString());
        Add(command, "$lastError", document.LastError);
        Add(command, "$chunkCount", document.ChunkCount);
        Add(command, "$createdAt", FormatDate(document.CreatedAt));
        Add(command, "$updatedAt", FormatDate(document.UpdatedAt));
    }

    private static KnowledgeDocument ReadDocument(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        ModuleId = reader.GetString(reader.GetOrdinal("module_id")),
        FileName = reader.GetString(reader.GetOrdinal("file_name")),
        FileType = reader.GetString(reader.GetOrdinal("file_type")),
        OriginalPath = reader.GetString(reader.GetOrdinal("original_path")),
        ExtractedPath = reader.GetString(reader.GetOrdinal("extracted_path")),
        ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
        Status = Enum.TryParse<KnowledgeDocumentStatus>(reader.GetString(reader.GetOrdinal("status")), out var status)
            ? status
            : KnowledgeDocumentStatus.Pending,
        LastError = reader.GetString(reader.GetOrdinal("last_error")),
        ChunkCount = reader.GetInt32(reader.GetOrdinal("chunk_count")),
        CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = ParseDate(reader.GetString(reader.GetOrdinal("updated_at")))
    };

    private static KnowledgeChunk ReadChunk(SqliteDataReader reader)
    {
        var pageOrdinal = reader.GetOrdinal("page_number");
        var blobOrdinal = reader.GetOrdinal("embedding_blob");
        return new KnowledgeChunk
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            DocumentId = reader.GetString(reader.GetOrdinal("document_id")),
            ModuleId = reader.GetString(reader.GetOrdinal("module_id")),
            ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index")),
            TitlePath = reader.GetString(reader.GetOrdinal("title_path")),
            PageNumber = reader.IsDBNull(pageOrdinal) ? null : reader.GetInt32(pageOrdinal),
            Content = reader.GetString(reader.GetOrdinal("content")),
            TokenCount = reader.GetInt32(reader.GetOrdinal("token_count")),
            EmbeddingModel = reader.GetString(reader.GetOrdinal("embedding_model")),
            EmbeddingDimension = reader.GetInt32(reader.GetOrdinal("embedding_dimension")),
            Embedding = reader.IsDBNull(blobOrdinal) ? null : DeserializeVector((byte[])reader.GetValue(blobOrdinal)),
            CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at")))
        };
    }

    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
}
