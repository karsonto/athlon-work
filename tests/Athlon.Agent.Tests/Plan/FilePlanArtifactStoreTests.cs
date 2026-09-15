using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Plan;

namespace Athlon.Agent.Tests.Plan;

public sealed class FilePlanArtifactStoreTests
{
    [Fact]
    public async Task SaveAsync_WritesMarkdownAndSnapshot_LoadAsync_RoundTrips()
    {
        var root = CreateTempRoot();
        var store = CreateStore(root);
        var run = new PlanRun
        {
            Id = "run-1",
            SessionId = "session-1",
            Goal = "Add token refresh",
            Title = "Token refresh",
            Todos = [new PlanTodoItem { Id = "impl", Content = "Implement" }]
        };

        await store.SaveAsync("session-1", CompletePlan, run);

        var sessionDir = SessionDir(root, "session-1");
        Assert.True(File.Exists(Path.Combine(sessionDir, FilePlanArtifactStore.MarkdownFileName)));
        Assert.True(File.Exists(Path.Combine(sessionDir, FilePlanArtifactStore.SnapshotFileName)));

        var loaded = await store.LoadAsync("session-1");
        Assert.NotNull(loaded);
        Assert.Contains("token refresh", loaded!.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Token refresh", loaded.Run!.Title);
        Assert.Equal("run-1", loaded.Run.Id);
        Assert.Equal("session-1", loaded.Run.SessionId);
        Assert.Equal("impl", Assert.Single(loaded.Run.Todos).Id);
        Assert.Equal(
            Path.Combine(sessionDir, FilePlanArtifactStore.MarkdownFileName),
            loaded.MarkdownPath);
    }

    [Fact]
    public async Task LoadAsync_WithoutArtifacts_ReturnsNull()
    {
        var root = CreateTempRoot();
        var store = CreateStore(root);

        Assert.Null(await store.LoadAsync("never-built"));
        Assert.Null(await store.LoadAsync(""));
    }

    [Fact]
    public async Task SaveAsync_WithoutRun_DerivesTitleStatusAndTodosFromMarkdown()
    {
        var root = CreateTempRoot();
        var store = CreateStore(root);

        await store.SaveAsync("session-1", CompletePlan, run: null);

        var loaded = await store.LoadAsync("session-1");
        Assert.NotNull(loaded);
        Assert.Equal("Fix auth token refresh", loaded!.Run!.Title);
        Assert.Equal(PlanRunStatuses.Approved, loaded.Run.Status);
        Assert.Equal(PlanPhase.Done, loaded.Run.Phase);
        Assert.NotEmpty(loaded.Run.Todos);
    }

    [Fact]
    public async Task LoadAsync_RecoversFromCorruptSnapshot()
    {
        var root = CreateTempRoot();
        var store = CreateStore(root);
        await store.SaveAsync("session-1", CompletePlan, run: null);

        await File.WriteAllTextAsync(
            Path.Combine(SessionDir(root, "session-1"), FilePlanArtifactStore.SnapshotFileName),
            "{ this is not json");

        var loaded = await store.LoadAsync("session-1");

        Assert.NotNull(loaded);
        Assert.Contains("token refresh", loaded!.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Null(loaded.Run);
    }

    [Fact]
    public async Task ClearAsync_RemovesBothArtifacts_AndIsIdempotent()
    {
        var root = CreateTempRoot();
        var store = CreateStore(root);
        await store.SaveAsync("session-1", CompletePlan, run: null);

        await store.ClearAsync("session-1");
        await store.ClearAsync("session-1");

        var sessionDir = SessionDir(root, "session-1");
        Assert.False(File.Exists(Path.Combine(sessionDir, FilePlanArtifactStore.MarkdownFileName)));
        Assert.False(File.Exists(Path.Combine(sessionDir, FilePlanArtifactStore.SnapshotFileName)));
        Assert.Null(await store.LoadAsync("session-1"));
    }

    [Fact]
    public async Task SaveAsync_IgnoresEmptyInputs()
    {
        var root = CreateTempRoot();
        var store = CreateStore(root);

        await store.SaveAsync("session-1", "   ", run: null);
        await store.SaveAsync("", CompletePlan, run: null);

        Assert.Null(await store.LoadAsync("session-1"));
    }

    private const string CompletePlan = """
        # Fix auth token refresh

        Refresh OAuth tokens before expiry so sessions do not drop mid-request.

        ## Steps
        1. Read the token store
        2. Add a refresh timer
        3. Cover with tests

        ## Acceptance
        - [ ] Tokens refresh before expiry
        - [ ] Tests pass
        """;

    private static string SessionDir(string root, string sessionId) =>
        Path.Combine(root, "sessions", sessionId);

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "athlon-plan-artifact-" + Guid.NewGuid().ToString("N"));

    private static FilePlanArtifactStore CreateStore(string root)
    {
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();
        return new FilePlanArtifactStore(paths, new JsonFileStore(), new AgentRunContextAccessor());
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

        public void EnsureCreated() => Directory.CreateDirectory(SessionsPath);

        public string ResolveSkillPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
