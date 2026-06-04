using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.SubAgents;

namespace Athlon.Agent.Tests;

public sealed class SubAgentSessionStoreTests
{
    [Fact]
    public async Task SaveAndLoad_PersistsUnderParentSubagentsDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-subagent-" + Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        var store = new FileSubAgentSessionStore(paths, new JsonFileStore());
        var parentId = "parent-1";
        var subId = "sub-1";
        var session = AgentSession.Create("Sub").WithWorkspace(@"C:\work") with { Id = subId };
        var bundle = new SubAgentSessionBundle(session, "Research assistant");

        await store.SaveAsync(parentId, subId, bundle);

        var expectedDir = Path.Combine(root, "sessions", parentId, "subagents", "default", subId);
        Assert.True(Directory.Exists(expectedDir));
        Assert.True(File.Exists(Path.Combine(expectedDir, "session.json")));
        Assert.True(File.Exists(Path.Combine(expectedDir, "meta.json")));

        var loaded = await store.LoadAsync(parentId, subId);
        Assert.NotNull(loaded);
        Assert.Equal("Research assistant", loaded!.Role);
        Assert.Equal(subId, loaded.Session.Id);
        Assert.Equal(@"C:\work", loaded.Session.ActiveWorkspace);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
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
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(AuditPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CredentialsPath);
            Directory.CreateDirectory(SkillsPath);
        }

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
                ? path
                : Path.Combine(SkillsPath, path);
    }
}
