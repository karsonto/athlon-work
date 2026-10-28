using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Knowledge;

namespace Athlon.Agent.Tests;

public sealed class KnowledgeStoreTests
{
    [Fact]
    public async Task SqliteKnowledgeStore_RoundTripsModulesDocumentsAndChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        var settings = new AppSettings();
        var store = new SqliteKnowledgeStore(paths, settings);

        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "????" });
        var document = await store.SaveDocumentAsync(new KnowledgeDocument
        {
            ModuleId = module.Id,
            FileName = "????.md",
            FileType = ".md",
            OriginalPath = Path.Combine(root, "????.md"),
            Status = KnowledgeDocumentStatus.Indexed
        });

        await store.ReplaceChunksAsync(document.Id, [
            new KnowledgeChunk
            {
                DocumentId = document.Id,
                ModuleId = module.Id,
                ChunkIndex = 0,
                TitlePath = "??",
                Content = "?? Endpoint ? API Key",
                EmbeddingModel = "test",
                EmbeddingDimension = 2,
                Embedding = [1, 0]
            }
        ]);
        var modules = await store.ListModulesAsync();
        var documents = await store.ListDocumentsAsync(module.Id);
        var chunks = await store.ListSearchableChunksAsync(new HashSet<string> { module.Id });

        Assert.Single(modules);
        Assert.Equal(1, modules[0].DocumentCount);
        Assert.Equal(1, modules[0].ChunkCount);
        Assert.Single(documents);
        Assert.Single(chunks);
        var embedding = Assert.IsType<float[]>(chunks[0].Embedding);
        Assert.Equal([1, 0], embedding);
    }

    [Fact]
    public async Task ListModulesAsync_counts_documents_and_chunks_with_subquery_sql()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-counts-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        var settings = new AppSettings();
        var store = new SqliteKnowledgeStore(paths, settings);

        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "????" });
        var docA = await store.SaveDocumentAsync(new KnowledgeDocument
        {
            ModuleId = module.Id,
            FileName = "a.md",
            FileType = ".md",
            OriginalPath = Path.Combine(root, "a.md"),
            Status = KnowledgeDocumentStatus.Indexed
        });
        var docB = await store.SaveDocumentAsync(new KnowledgeDocument
        {
            ModuleId = module.Id,
            FileName = "b.md",
            FileType = ".md",
            OriginalPath = Path.Combine(root, "b.md"),
            Status = KnowledgeDocumentStatus.Indexed
        });

        await store.ReplaceChunksAsync(docA.Id, [
            new KnowledgeChunk { DocumentId = docA.Id, ModuleId = module.Id, ChunkIndex = 0, Content = "a1", EmbeddingModel = "t", EmbeddingDimension = 2, Embedding = [1, 0] },
            new KnowledgeChunk { DocumentId = docA.Id, ModuleId = module.Id, ChunkIndex = 1, Content = "a2", EmbeddingModel = "t", EmbeddingDimension = 2, Embedding = [0, 1] }
        ]);
        await store.ReplaceChunksAsync(docB.Id, [
            new KnowledgeChunk { DocumentId = docB.Id, ModuleId = module.Id, ChunkIndex = 0, Content = "b1", EmbeddingModel = "t", EmbeddingDimension = 2, Embedding = [1, 1] }
        ]);

        var modules = await store.ListModulesAsync();

        Assert.Single(modules);
        Assert.Equal(2, modules[0].DocumentCount);
        Assert.Equal(3, modules[0].ChunkCount);
    }

    [Fact]
    public async Task KnowledgeSearchService_FiltersBySessionModulesAndRanksByCosine()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-search-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        var settings = new AppSettings();
        settings.Knowledge.Search.MinScore = 0;
        var store = new SqliteKnowledgeStore(paths, settings);
        var moduleA = await store.SaveModuleAsync(new KnowledgeModule { Name = "?? A" });
        var moduleB = await store.SaveModuleAsync(new KnowledgeModule { Name = "?? B" });
        var docA = await store.SaveDocumentAsync(new KnowledgeDocument { ModuleId = moduleA.Id, FileName = "a.md", FileType = ".md" });
        var docB = await store.SaveDocumentAsync(new KnowledgeDocument { ModuleId = moduleB.Id, FileName = "b.md", FileType = ".md" });
        await store.ReplaceChunksAsync(docA.Id, [
            new KnowledgeChunk { DocumentId = docA.Id, ModuleId = moduleA.Id, Content = "????", EmbeddingModel = "test", EmbeddingDimension = 2, Embedding = [1, 0] }
        ]);
        await store.ReplaceChunksAsync(docB.Id, [
            new KnowledgeChunk { DocumentId = docB.Id, ModuleId = moduleB.Id, Content = "????", EmbeddingModel = "test", EmbeddingDimension = 2, Embedding = [1, 0] }
        ]);

        var sessionKnowledgeState = new SessionKnowledgeState(paths, new JsonFileStore(), new AgentRunContextAccessor());
        await sessionKnowledgeState.SaveAsync("session-1", new SessionKnowledgeSnapshot(true, new HashSet<string> { moduleA.Id }));

        var service = new KnowledgeSearchService(store, new FakeEmbeddingClient([1, 0]), sessionKnowledgeState, settings);
        var hits = await service.SearchAsync("session-1", "query");

        Assert.Single(hits);
        Assert.Equal("?? A", hits[0].ModuleName);
        Assert.Equal("a.md", hits[0].FileName);
    }

    [Fact]
    public async Task KnowledgeSearchService_SearchInScope_FiltersByDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-document-search-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        var settings = new AppSettings();
        settings.Knowledge.Search.MinScore = 0;
        var store = new SqliteKnowledgeStore(paths, settings);
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "??" });
        var docA = await store.SaveDocumentAsync(new KnowledgeDocument { ModuleId = module.Id, FileName = "a.md", FileType = ".md" });
        var docB = await store.SaveDocumentAsync(new KnowledgeDocument { ModuleId = module.Id, FileName = "b.md", FileType = ".md" });
        await store.ReplaceChunksAsync(docA.Id, [
            new KnowledgeChunk { DocumentId = docA.Id, ModuleId = module.Id, Content = "?? A ??", EmbeddingModel = "test", EmbeddingDimension = 2, Embedding = [1, 0] }
        ]);
        await store.ReplaceChunksAsync(docB.Id, [
            new KnowledgeChunk { DocumentId = docB.Id, ModuleId = module.Id, Content = "?? B ??", EmbeddingModel = "test", EmbeddingDimension = 2, Embedding = [1, 0] }
        ]);

        var service = new KnowledgeSearchService(store, new FakeEmbeddingClient([1, 0]), RouterTestDependencies.CreateSessionKnowledgeState(), settings);
        var hits = await service.SearchInScopeAsync(
            "query",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { module.Id },
            docB.Id);

        Assert.Single(hits);
        Assert.Equal("b.md", hits[0].FileName);
    }

    [Fact]
    public async Task KnowledgeIndexerService_ReportsEmbeddingProgressByBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-index-progress-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        settings.Knowledge.Embedding.BatchSize = 2;
        settings.Knowledge.Chunking.TargetChars = 500;
        settings.Knowledge.Chunking.OverlapChars = 0;
        var store = new SqliteKnowledgeStore(paths, settings);
        var indexer = new KnowledgeIndexerService(
            paths,
            settings,
            store,
            new FakeEmbeddingClient([1, 0]),
            new KnowledgeDocumentExtractor(settings, new NoKnowledgeOcr(), new NoOpLogger()),
            new KnowledgeChunker(settings),
            new EmptyGraphExtractor(),
            new NoOpLogger());
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "??" });
        var sourcePath = Path.Combine(root, "source.md");
        await File.WriteAllTextAsync(sourcePath, new string('a', 1200));
        var reports = new List<KnowledgeIndexingProgress>();

        var document = await indexer.ImportDocumentAsync(
            module.Id,
            sourcePath,
            progress: new RecordingProgress(reports));

        Assert.Equal(3, document.ChunkCount);
        const string embeddingStage = "\u5411\u91CF\u5316";
        var embeddingReports = reports.Where(report => report.Stage == embeddingStage).ToArray();
        Assert.Contains(embeddingReports, report => report.Processed == 2);
        Assert.Contains(embeddingReports, report => report.Processed >= 3);
        Assert.Equal(100, reports.Last().Percent);
    }

    [Fact]
    public async Task ImportDocumentAsync_WhenExtractionFails_DoesNotPersistDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-import-fail-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var store = new SqliteKnowledgeStore(paths, settings);
        var indexer = new KnowledgeIndexerService(
            paths,
            settings,
            store,
            new FakeEmbeddingClient([1, 0]),
            new KnowledgeDocumentExtractor(settings, new NoKnowledgeOcr(), new NoOpLogger()),
            new KnowledgeChunker(settings),
            new EmptyGraphExtractor(),
            new NoOpLogger());
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "??" });
        var sourcePath = Path.Combine(root, "empty.txt");
        await File.WriteAllTextAsync(sourcePath, "   ");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.ImportDocumentAsync(module.Id, sourcePath));

        var documents = await store.ListDocumentsAsync(module.Id);
        Assert.Empty(documents);
        Assert.False(Directory.EnumerateFiles(store.GetOriginalsDirectory(module.Id), "*", SearchOption.TopDirectoryOnly).Any());
        Assert.False(Directory.EnumerateFiles(store.GetExtractedDirectory(module.Id), "*", SearchOption.TopDirectoryOnly).Any());
    }

    [Fact]
    public async Task CommitIndexedDocumentAsync_WritesDocumentAndChunksAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-commit-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var store = new SqliteKnowledgeStore(paths, settings);
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "??" });
        var document = new KnowledgeDocument
        {
            ModuleId = module.Id,
            FileName = "demo.md",
            FileType = ".md",
            OriginalPath = Path.Combine(root, "demo.md"),
            ExtractedPath = Path.Combine(root, "demo.extracted.md"),
            Status = KnowledgeDocumentStatus.Indexed,
            ChunkCount = 1
        };
        var chunks = new[]
        {
            new KnowledgeChunk
            {
                DocumentId = document.Id,
                ModuleId = module.Id,
                Content = "hello",
                EmbeddingModel = "test",
                EmbeddingDimension = 2,
                Embedding = [1, 0]
            }
        };

        await store.CommitIndexedDocumentAsync(document, chunks);

        var saved = await store.GetDocumentAsync(document.Id);
        Assert.NotNull(saved);
        Assert.Equal(KnowledgeDocumentStatus.Indexed, saved!.Status);
        var searchable = await store.ListSearchableChunksAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { module.Id });
        Assert.Single(searchable);
    }

    [Fact]
    public async Task CommitIndexedDocumentAsync_ReplacesGraphAndDropsOrphansWhenDocumentIsDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-graph-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        var settings = new AppSettings();
        var store = new SqliteKnowledgeStore(paths, settings);
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "space" });
        var first = await store.CommitIndexedDocumentAsync(
            new KnowledgeDocument
            {
                ModuleId = module.Id,
                FileName = "a.md",
                FileType = ".md",
                Status = KnowledgeDocumentStatus.Indexed,
                ChunkCount = 1
            },
            [
                new KnowledgeChunk
                {
                    Id = "chunk-a",
                    DocumentId = "",
                    ModuleId = module.Id,
                    Content = "alice bio",
                    EmbeddingModel = "test",
                    EmbeddingDimension = 2,
                    Embedding = [1, 0]
                }
            ],
            graph: GraphWith("alice", "bob", "chunk-a"));

        var entities = await store.ListEntitiesAsync(new HashSet<string> { module.Id });
        Assert.Equal(2, entities.Count);
        Assert.Single(await store.ListRelationsAsync(new HashSet<string> { module.Id }));

        var second = await store.CommitIndexedDocumentAsync(
            new KnowledgeDocument
            {
                ModuleId = module.Id,
                FileName = "b.md",
                FileType = ".md",
                Status = KnowledgeDocumentStatus.Indexed,
                ChunkCount = 1
            },
            [
                new KnowledgeChunk
                {
                    Id = "chunk-b",
                    ModuleId = module.Id,
                    Content = "carol bio",
                    EmbeddingModel = "test",
                    EmbeddingDimension = 2,
                    Embedding = [0, 1]
                }
            ],
            graph: new DocumentGraphCommit
            {
                Status = KnowledgeGraphStatus.Indexed,
                Entities = [Entity("Carol", "person", "Carol", [0, 1], "chunk-b")]
            });

        await store.CommitIndexedDocumentAsync(
            first,
            [
                new KnowledgeChunk
                {
                    Id = "chunk-a2",
                    ModuleId = module.Id,
                    Content = "alice again",
                    EmbeddingModel = "test",
                    EmbeddingDimension = 2,
                    Embedding = [1, 0]
                }
            ],
            graph: GraphWith("alice", "bob", "chunk-a2"));

        entities = await store.ListEntitiesAsync(new HashSet<string> { module.Id });
        Assert.Equal(3, entities.Select(entity => entity.NormalizedName).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        await store.DeleteDocumentAsync(first.Id);
        entities = await store.ListEntitiesAsync(new HashSet<string> { module.Id });
        Assert.Single(entities);
        Assert.Equal("carol", entities[0].NormalizedName);

        await store.DeleteDocumentAsync(second.Id);
        Assert.Empty(await store.ListEntitiesAsync(new HashSet<string> { module.Id }));
        Assert.Empty(await store.ListRelationsAsync(new HashSet<string> { module.Id }));
    }

    [Fact]
    public async Task KnowledgeSearchService_MergesVectorHitsWithOneHopGraphChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-graph-search-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        var settings = new AppSettings();
        settings.Knowledge.Search.MinScore = 0.25;
        var store = new SqliteKnowledgeStore(paths, settings);
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "space" });
        await store.CommitIndexedDocumentAsync(
            new KnowledgeDocument
            {
                ModuleId = module.Id,
                FileName = "notes.md",
                FileType = ".md",
                Status = KnowledgeDocumentStatus.Indexed,
                ChunkCount = 3
            },
            [
                new KnowledgeChunk { Id = "policy", ModuleId = module.Id, ChunkIndex = 0, Content = "policy", EmbeddingModel = "t", EmbeddingDimension = 2, Embedding = [1, 0] },
                new KnowledgeChunk { Id = "alice", ModuleId = module.Id, ChunkIndex = 1, Content = "alice bio", EmbeddingModel = "t", EmbeddingDimension = 2, Embedding = [0, 1] },
                new KnowledgeChunk { Id = "bob", ModuleId = module.Id, ChunkIndex = 2, Content = "bob bio", EmbeddingModel = "t", EmbeddingDimension = 2, Embedding = [0, 1] }
            ],
            graph: new DocumentGraphCommit
            {
                Status = KnowledgeGraphStatus.Indexed,
                Entities =
                [
                    Entity("Alice", "person", "A founder", [0, 1], "alice"),
                    Entity("Bob", "person", "A partner", [0, 1], "bob")
                ],
                Relations =
                [
                    new GraphRelationDraft
                    {
                        SourceNormalizedName = "alice",
                        SourceType = "person",
                        TargetNormalizedName = "bob",
                        TargetType = "person",
                        Description = "knows",
                        Weight = 6,
                        Evidence = "friends",
                        ChunkId = "alice"
                    }
                ]
            });

        var service = new KnowledgeSearchService(store, new FakeEmbeddingClient([1, 0]), RouterTestDependencies.CreateSessionKnowledgeState(), settings);
        var hits = await service.SearchInScopeAsync(
            "Tell me about Alice",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { module.Id });

        Assert.Equal(3, hits.Count);
        Assert.Equal("policy", hits[0].Content);
        Assert.Contains(hits, hit => hit.Content == "alice bio" && hit.RelationSummary.Contains("Alice —knows→ Bob", StringComparison.Ordinal));
        Assert.Contains(hits, hit => hit.Content == "bob bio");
        Assert.All(hits.Skip(1), hit => Assert.True(hit.Score >= 0.25));
    }

    [Fact]
    public async Task ImportDocumentAsync_WhenGraphExtractionFails_StillPersistsChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-graph-fail-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        settings.Knowledge.Graph.Enabled = true;
        var store = new SqliteKnowledgeStore(paths, settings);
        var indexer = new KnowledgeIndexerService(
            paths,
            settings,
            store,
            new FakeEmbeddingClient([1, 0]),
            new KnowledgeDocumentExtractor(settings, new NoKnowledgeOcr(), new NoOpLogger()),
            new KnowledgeChunker(settings),
            new ThrowingGraphExtractor(),
            new NoOpLogger());
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "space" });
        var sourcePath = Path.Combine(root, "source.md");
        await File.WriteAllTextAsync(sourcePath, "Acme was founded by Ada.");

        var document = await indexer.ImportDocumentAsync(module.Id, sourcePath);

        Assert.Equal(KnowledgeDocumentStatus.Indexed, document.Status);
        Assert.Equal(KnowledgeGraphStatus.Failed, document.GraphStatus);
        Assert.Contains("boom", document.GraphError, StringComparison.Ordinal);
        Assert.NotEmpty(await store.ListSearchableChunksAsync(new HashSet<string> { module.Id }));
        Assert.Empty(await store.ListEntitiesAsync(new HashSet<string> { module.Id }));
    }

    [Fact]
    public async Task RebuildModuleGraphAsync_ReplacesEntitiesWithoutReembeddingChunkText()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-knowledge-graph-rebuild-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        settings.Knowledge.Embedding.Model = "chunk-model";
        var store = new SqliteKnowledgeStore(paths, settings);
        var embeddings = new RecordingEmbeddingClient([1, 0]);
        var indexer = new KnowledgeIndexerService(
            paths,
            settings,
            store,
            embeddings,
            new KnowledgeDocumentExtractor(settings, new NoKnowledgeOcr(), new NoOpLogger()),
            new KnowledgeChunker(settings),
            new NamedEntityExtractor("Acme", "organization"),
            new NoOpLogger());
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "space" });
        var sourcePath = Path.Combine(root, "source.md");
        await File.WriteAllTextAsync(sourcePath, "hello knowledge");
        var document = await indexer.ImportDocumentAsync(module.Id, sourcePath);
        var chunkModel = (await store.ListDocumentChunksAsync(document.Id)).Single().EmbeddingModel;
        embeddings.Texts.Clear();

        var rebuild = new KnowledgeIndexerService(
            paths,
            settings,
            store,
            embeddings,
            new KnowledgeDocumentExtractor(settings, new NoKnowledgeOcr(), new NoOpLogger()),
            new KnowledgeChunker(settings),
            new NamedEntityExtractor("Beta", "person"),
            new NoOpLogger());
        var result = await rebuild.RebuildModuleGraphAsync(module.Id);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        var entities = await store.ListEntitiesAsync(new HashSet<string> { module.Id });
        Assert.Single(entities);
        Assert.Equal("beta", entities[0].NormalizedName);
        Assert.Equal(chunkModel, (await store.ListDocumentChunksAsync(document.Id)).Single().EmbeddingModel);
        Assert.DoesNotContain(embeddings.Texts, text => text.Contains("hello knowledge", StringComparison.Ordinal));
        Assert.Contains(embeddings.Texts, text => text.Contains("Beta", StringComparison.Ordinal));
    }

    private static DocumentGraphCommit GraphWith(string source, string target, string chunkId) => new()
    {
        Status = KnowledgeGraphStatus.Indexed,
        Entities =
        [
            Entity(source, "person", source, [1, 0], chunkId),
            Entity(target, "organization", target, [0, 1], chunkId)
        ],
        Relations =
        [
            new GraphRelationDraft
            {
                SourceNormalizedName = source,
                SourceType = "person",
                TargetNormalizedName = target,
                TargetType = "organization",
                Description = "works at",
                Weight = 4,
                Evidence = "employed",
                ChunkId = chunkId
            }
        ]
    };

    private static GraphEntityDraft Entity(string name, string type, string description, float[] embedding, string chunkId) => new()
    {
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        EntityType = type,
        Description = description,
        Embedding = embedding,
        EmbeddingDimension = embedding.Length,
        EmbeddingModel = "test",
        ChunkIds = [chunkId]
    };

    private sealed class FakeEmbeddingClient(float[] vector) : IEmbeddingClient
    {
        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EmbeddingVector>>(texts.Select(text => new EmbeddingVector(text, vector)).ToArray());
    }

    private sealed class RecordingEmbeddingClient(float[] vector) : IEmbeddingClient
    {
        public List<string> Texts { get; } = [];

        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Texts.AddRange(texts);
            return Task.FromResult<IReadOnlyList<EmbeddingVector>>(texts.Select(text => new EmbeddingVector(text, vector)).ToArray());
        }
    }

    private sealed class EmptyGraphExtractor : IKnowledgeGraphExtractor
    {
        public Task<KnowledgeGraphExtraction> ExtractAsync(
            IReadOnlyList<KnowledgeChunk> chunks,
            IReadOnlyList<string> entityTypes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgeGraphExtraction([], []));
    }

    private sealed class ThrowingGraphExtractor : IKnowledgeGraphExtractor
    {
        public Task<KnowledgeGraphExtraction> ExtractAsync(
            IReadOnlyList<KnowledgeChunk> chunks,
            IReadOnlyList<string> entityTypes,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class NamedEntityExtractor(string name, string type) : IKnowledgeGraphExtractor
    {
        public Task<KnowledgeGraphExtraction> ExtractAsync(
            IReadOnlyList<KnowledgeChunk> chunks,
            IReadOnlyList<string> entityTypes,
            CancellationToken cancellationToken = default)
        {
            var chunk = chunks[0];
            return Task.FromResult(new KnowledgeGraphExtraction(
                [new ExtractedKnowledgeEntity(chunk.Id, name, name.ToLowerInvariant(), type, "Described")],
                []));
        }
    }

    private sealed class NoKnowledgeOcr : IKnowledgePageOcr
    {
        public Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
            IReadOnlyList<KnowledgeOcrPageImage> pages,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }

    private sealed class RecordingProgress(List<KnowledgeIndexingProgress> reports) : IProgress<KnowledgeIndexingProgress>
    {
        public void Report(KnowledgeIndexingProgress value) => reports.Add(value);
    }

    private sealed class TestPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated()
        {
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(AuditPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CredentialsPath);
            Directory.CreateDirectory(SkillsPath);
        }

        public string ResolveSkillPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
