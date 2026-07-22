using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Ssh;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class SshWorkspaceGuardAndToolTests
{
    [Fact]
    public void WorkspaceGuard_Ssh_NormalizesUnixPathsWithoutGetFullPath()
    {
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace("/home/u/proj", WorkspaceKind.Ssh, "ws1", "proj");
        var guard = new WorkspaceGuard(
            context,
            new AgentRunContextAccessor(),
            new AppSettings(),
            new TestPathProvider(Path.Combine(Path.GetTempPath(), ".athlon-agent")));

        Assert.Equal(WorkspaceKind.Ssh, guard.CurrentKind);
        Assert.Equal("/home/u/proj/src/a.cs", guard.Normalize("src/a.cs"));
        Assert.True(guard.IsInsideWorkspace("/home/u/proj/src/a.cs"));
        Assert.False(guard.IsInsideWorkspace("/tmp/x"));
    }

    [Fact]
    public async Task SshFileReadTool_FailsWhenNotConnected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-ssh-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace("/home/u/proj", WorkspaceKind.Ssh, "ws1", "proj");
        var paths = new TestPathProvider(root);
        var guard = new WorkspaceGuard(context, new AgentRunContextAccessor(), new AppSettings(), paths);
        var client = new DisconnectedSshClient();
        var tool = new SshFileReadTool(
            guard,
            client,
            new AuditLogService(new NoOpLogger(), paths, new JsonFileStore()),
            new AppSettings());

        var result = await tool.InvokeAsync(new ToolInvocation("file_read", new Dictionary<string, string>
        {
            ["path"] = "README.md"
        }));

        Assert.False(result.Succeeded);
        Assert.Contains("not connected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompositeToolRouter_FiltersLocalAndRemoteToolsByWorkspaceKind()
    {
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace("/home/u/proj", WorkspaceKind.Ssh, "ws1", "proj");
        var guard = new WorkspaceGuard(
            context,
            new AgentRunContextAccessor(),
            new AppSettings(),
            new TestPathProvider(Path.Combine(Path.GetTempPath(), ".athlon-agent")));

        var local = new MarkerLocalTool();
        var remote = new MarkerRemoteTool();
        var router = new CompositeToolRouter(
            [local, remote],
            new EmptyMcpRegistry(),
            new AppSettings(),
            new ActiveAgentSessionContext(),
            new StubKnowledgeState(),
            new StubHarnessState(),
            new AgentRunContextAccessor(),
            guard);

        var names = router.ListTools().Select(tool => tool.Name).ToArray();
        Assert.Contains("remote_marker", names);
        Assert.DoesNotContain("local_marker", names);

        context.SetWorkspace(Path.GetTempPath(), WorkspaceKind.Local, null, "local");
        names = router.ListTools().Select(tool => tool.Name).ToArray();
        Assert.Contains("local_marker", names);
        Assert.DoesNotContain("remote_marker", names);
    }

    private sealed class DisconnectedSshClient : ISshWorkspaceClient
    {
        public bool IsConnected => false;
        public string? RemoteRoot => null;
        public string? ConnectedWorkspaceId => null;
        public Task ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<SshFileInfo> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SSH not connected");
        public Task<SshFileInfo?> TryGetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<SshFileInfo?>(null);
        public Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SSH not connected");
        public Task<T> ReadViaStreamAsync<T>(
            string remotePath,
            Func<Stream, CancellationToken, Task<T>> reader,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SSH not connected");
        public Task WriteTextAsync(string remotePath, string content, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SSH not connected");
        public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SSH not connected");
        public async IAsyncEnumerable<SshEntry> ListAsync(
            string remotePath,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<SshCommandResult> ExecuteAsync(
            string command,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SSH not connected");

        public Task<bool> HasCommandAsync(string commandName, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class MarkerLocalTool : IAgentTool, ILocalWorkspaceTool
    {
        public ToolDefinition Definition { get; } = new("local_marker", "local", ToolSchema.Object().Build());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class MarkerRemoteTool : IAgentTool, IRemoteWorkspaceTool
    {
        public ToolDefinition Definition { get; } = new("remote_marker", "remote", ToolSchema.Object().Build());
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class EmptyMcpRegistry : IMcpRegistry
    {
        public int CatalogVersion => 0;
        public int CatalogCount => 0;
        public int CatalogSchemaCharCount => 0;
        public IReadOnlyList<McpServerStatus> GetStatuses() => [];
        public IReadOnlyList<ToolDefinition> ListToolDefinitions() => [];
        public IReadOnlyList<McpCatalogEntry> ListCatalogEntries() => [];
        public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(
            string query,
            int topK,
            double minScore,
            string? serverName = null) => [];
        public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<ToolResult> InvokeAsync(
            string serverName,
            string toolName,
            ToolCallArguments args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Failure("none", "none"));
    }

    private sealed class StubKnowledgeState : ISessionKnowledgeState
    {
        public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(string sessionId, SessionKnowledgeSnapshot state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public SessionKnowledgeSnapshot GetSnapshot(string? sessionId) => SessionKnowledgeSnapshot.Empty;
        public bool ShouldExposeKnowledgeTool(string? sessionId) => false;
        public Task<IReadOnlySet<string>> GetModuleIdsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class StubHarnessState : ISessionHarnessState
    {
        public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public SessionHarnessSnapshot GetSnapshot(string? sessionId) => SessionHarnessSnapshot.Empty;
        public SessionAgentMode GetMode(string? sessionId) => SessionAgentMode.Agent;
        public bool IsCodingMode(string? sessionId) => true;
        public bool IsAskMode(string? sessionId) => false;
        public bool IsPlanMode(string? sessionId) => false;
        public bool IsEnabled(string? sessionId) => true;
        public bool IsCodingModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => true;
        public bool IsAskModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => false;
        public bool IsPlanModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => false;
        public bool IsEnabledForActiveRun(IAgentRunContextAccessor runContextAccessor) => true;
    }

    private sealed class TestPathProvider(string rootPath) : IAppPathProvider
    {
        public string RootPath { get; } = rootPath;
        public string ConfigPath => Path.Combine(rootPath, "config");
        public string SessionsPath => Path.Combine(rootPath, "sessions");
        public string AuditPath => Path.Combine(rootPath, "audit");
        public string LogsPath => Path.Combine(rootPath, "logs");
        public string CredentialsPath => Path.Combine(rootPath, "credentials");
        public string SkillsPath => Path.Combine(rootPath, "skills");
        public void EnsureCreated() { }
        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
