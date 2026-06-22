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

        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "产品资料" });
        var document = await store.SaveDocumentAsync(new KnowledgeDocument
        {
            ModuleId = module.Id,
            FileName = "产品说明.md",
            FileType = ".md",
            OriginalPath = Path.Combine(root, "产品说明.md"),
            Status = KnowledgeDocumentStatus.Indexed
        });

        await store.ReplaceChunksAsync(document.Id, [
            new KnowledgeChunk
            {
                DocumentId = document.Id,
                ModuleId = module.Id,
                ChunkIndex = 0,
                TitlePath = "配置",
                Content = "配置 Endpoint 和 API Key",
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

        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "计数测试" });
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
        var moduleA = await store.SaveModuleAsync(new KnowledgeModule { Name = "模块 A" });
        var moduleB = await store.SaveModuleAsync(new KnowledgeModule { Name = "模块 B" });
        var docA = await store.SaveDocumentAsync(new KnowledgeDocument { ModuleId = moduleA.Id, FileName = "a.md", FileType = ".md" });
        var docB = await store.SaveDocumentAsync(new KnowledgeDocument { ModuleId = moduleB.Id, FileName = "b.md", FileType = ".md" });
        await store.ReplaceChunksAsync(docA.Id, [
            new KnowledgeChunk { DocumentId = docA.Id, ModuleId = moduleA.Id, Content = "命中内容", EmbeddingModel = "test", EmbeddingDimension = 2, Embedding = [1, 0] }
        ]);
        await store.ReplaceChunksAsync(docB.Id, [
            new KnowledgeChunk { DocumentId = docB.Id, ModuleId = moduleB.Id, Content = "不应返回", EmbeddingModel = "test", EmbeddingDimension = 2, Embedding = [1, 0] }
        ]);

        var sessionKnowledgeState = new SessionKnowledgeState(paths, new JsonFileStore());
        await sessionKnowledgeState.SaveAsync("session-1", new SessionKnowledgeSnapshot(true, new HashSet<string> { moduleA.Id }));

        var service = new KnowledgeSearchService(store, new FakeEmbeddingClient([1, 0]), sessionKnowledgeState, settings);
        var hits = await service.SearchAsync("session-1", "query");

        Assert.Single(hits);
        Assert.Equal("模块 A", hits[0].ModuleName);
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
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "模块" });
        var docA = await store.SaveDocumentAsync(new KnowledgeDocument { ModuleId = module.Id, FileName = "a.md", FileType = ".md" });
        var docB = await store.SaveDocumentAsync(new KnowledgeDocument { ModuleId = module.Id, FileName = "b.md", FileType = ".md" });
        await store.ReplaceChunksAsync(docA.Id, [
            new KnowledgeChunk { DocumentId = docA.Id, ModuleId = module.Id, Content = "文档 A 内容", EmbeddingModel = "test", EmbeddingDimension = 2, Embedding = [1, 0] }
        ]);
        await store.ReplaceChunksAsync(docB.Id, [
            new KnowledgeChunk { DocumentId = docB.Id, ModuleId = module.Id, Content = "文档 B 内容", EmbeddingModel = "test", EmbeddingDimension = 2, Embedding = [1, 0] }
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
            new KnowledgeDocumentExtractor(),
            new KnowledgeChunker(settings),
            new NoOpLogger());
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "模块" });
        var sourcePath = Path.Combine(root, "source.md");
        await File.WriteAllTextAsync(sourcePath, new string('a', 1200));
        var reports = new List<KnowledgeIndexingProgress>();

        var document = await indexer.ImportDocumentAsync(
            module.Id,
            sourcePath,
            progress: new RecordingProgress(reports));

        Assert.Equal(3, document.ChunkCount);
        var embeddingReports = reports.Where(report => report.Stage == "向量化").ToArray();
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
            new KnowledgeDocumentExtractor(),
            new KnowledgeChunker(settings),
            new NoOpLogger());
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "模块" });
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
        var module = await store.SaveModuleAsync(new KnowledgeModule { Name = "模块" });
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

    private sealed class FakeEmbeddingClient(float[] vector) : IEmbeddingClient
    {
        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EmbeddingVector>>(texts.Select(text => new EmbeddingVector(text, vector)).ToArray());
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
