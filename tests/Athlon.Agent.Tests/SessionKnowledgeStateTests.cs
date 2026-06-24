using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Knowledge;

namespace Athlon.Agent.Tests;

public sealed class SessionKnowledgeStateTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefaults_WhenFileMissing()
    {
        var root = CreateTempRoot();
        var state = CreateState(root);

        await state.LoadAsync("session-1");

        var snapshot = state.GetSnapshot("session-1");
        Assert.False(snapshot.Enabled);
        Assert.Empty(snapshot.ModuleIds);
        Assert.False(state.ShouldExposeKnowledgeTool("session-1"));
    }

    [Fact]
    public async Task SaveAsync_PersistsEnabledAndModuleIds()
    {
        var root = CreateTempRoot();
        var state = CreateState(root);
        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "module-a", "module-b" };

        await state.SaveAsync("session-1", new SessionKnowledgeSnapshot(true, moduleIds));
        await state.LoadAsync("session-2");

        var reloaded = CreateState(root);
        await reloaded.LoadAsync("session-1");
        var snapshot = reloaded.GetSnapshot("session-1");

        Assert.True(snapshot.Enabled);
        Assert.Equal(2, snapshot.ModuleIds.Count);
        Assert.Contains("module-a", snapshot.ModuleIds);
        Assert.Contains("module-b", snapshot.ModuleIds);
        Assert.True(reloaded.ShouldExposeKnowledgeTool("session-1"));
    }

    [Fact]
    public async Task SaveAsync_PreservesModuleIds_WhenDisabled()
    {
        var root = CreateTempRoot();
        var state = CreateState(root);
        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "module-a" };

        await state.SaveAsync("session-1", new SessionKnowledgeSnapshot(true, moduleIds));
        await state.SaveAsync("session-1", new SessionKnowledgeSnapshot(false, moduleIds));

        var snapshot = state.GetSnapshot("session-1");
        Assert.False(snapshot.Enabled);
        Assert.Single(snapshot.ModuleIds);
        Assert.False(state.ShouldExposeKnowledgeTool("session-1"));

        var moduleIdsFromSearch = await state.GetModuleIdsAsync("session-1");
        Assert.Empty(moduleIdsFromSearch);
    }

    [Fact]
    public async Task GetModuleIdsAsync_LoadsFromDisk_WhenNotCached()
    {
        var root = CreateTempRoot();
        var writer = CreateState(root);
        await writer.SaveAsync("session-1", new SessionKnowledgeSnapshot(true, new HashSet<string> { "module-a" }));

        var reader = CreateState(root);
        var moduleIds = await reader.GetModuleIdsAsync("session-1");

        Assert.Single(moduleIds);
        Assert.Contains("module-a", moduleIds);
    }

    private static SessionKnowledgeState CreateState(string root)
    {
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        return new SessionKnowledgeState(paths, new JsonFileStore(), new AgentRunContextAccessor());
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "athlon-session-knowledge-" + Guid.NewGuid().ToString("N"));

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
