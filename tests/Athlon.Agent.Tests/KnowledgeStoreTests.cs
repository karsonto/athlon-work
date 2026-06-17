using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Knowledge;

namespace Athlon.Agent.Tests;

public sealed class KnowledgeStoreTests
{
    [Fact]
    public async Task SqliteKnowledgeStore_RoundTripsModulesDocumentsChunksAndSessionSelection()
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
        await store.SaveSessionSelectionAsync("session-1", new HashSet<string> { module.Id });

        var modules = await store.ListModulesAsync();
        var documents = await store.ListDocumentsAsync(module.Id);
        var chunks = await store.ListSearchableChunksAsync(new HashSet<string> { module.Id });
        var selection = await store.GetSessionSelectionAsync("session-1");

        Assert.Single(modules);
        Assert.Equal(1, modules[0].DocumentCount);
        Assert.Equal(1, modules[0].ChunkCount);
        Assert.Single(documents);
        Assert.Single(chunks);
        var embedding = Assert.IsType<float[]>(chunks[0].Embedding);
        Assert.Equal([1, 0], embedding);
        Assert.Contains(module.Id, selection);
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
        await store.SaveSessionSelectionAsync("session-1", new HashSet<string> { moduleA.Id });

        var service = new KnowledgeSearchService(store, new FakeEmbeddingClient([1, 0]), settings);
        var hits = await service.SearchAsync("session-1", "query");

        Assert.Single(hits);
        Assert.Equal("模块 A", hits[0].ModuleName);
        Assert.Equal("a.md", hits[0].FileName);
    }

    private sealed class FakeEmbeddingClient(float[] vector) : IEmbeddingClient
    {
        public Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EmbeddingVector>>(texts.Select(text => new EmbeddingVector(text, vector)).ToArray());
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
