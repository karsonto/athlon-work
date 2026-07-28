using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Tests;

internal static class ComposerTestFactory
{
    public static ComposerSlashCommandRegistry CreateSlashRegistry(params IComposerSlashCommand[] commands) =>
        new(commands);

    public static ComposerSlashCommandExecutor CreateSlashExecutor(IComposerSlashCommandRegistry? registry = null) =>
        new(registry ?? CreateSlashRegistry());

    public static ComposerAtCompletionService CreateCompletionService(
        IMcpRegistry? mcpRegistry = null,
        IComposerSlashCommandRegistry? slashRegistry = null,
        ISshWorkspaceClient? sshClient = null,
        IActiveWorkspaceContext? workspaceContext = null) =>
        new(
            mcpRegistry ?? new TestMcpRegistry(),
            slashRegistry ?? CreateSlashRegistry(),
            sshClient ?? new DisconnectedSshClient(),
            workspaceContext ?? new LocalWorkspaceContext());

    public static ComposerCoordinator CreateCoordinator(
        IAgentSkillCatalog? skillCatalog = null,
        AppSettings? settings = null,
        IMcpRegistry? mcpRegistry = null,
        IComposerSlashCommandRegistry? slashRegistry = null,
        ComposerAtCompletionService? completionService = null)
    {
        slashRegistry ??= CreateSlashRegistry();
        completionService ??= CreateCompletionService(mcpRegistry, slashRegistry);
        return new ComposerCoordinator(
            completionService,
            slashRegistry,
            CreateSlashExecutor(slashRegistry),
            skillCatalog ?? new StubSkillCatalog([]),
            settings ?? new AppSettings(),
            new StubImageAttachmentStore(),
            new AppPathProvider());
    }

    internal sealed class LocalWorkspaceContext : IActiveWorkspaceContext
    {
        public string? RootPath { get; private set; }
        public string? DisplayName { get; private set; }
        public IReadOnlyList<string> IgnorePatterns { get; private set; } = Array.Empty<string>();
        public WorkspaceKind Kind { get; private set; } = WorkspaceKind.Local;
        public string? WorkspaceId { get; private set; }

        public void SetWorkspace(string? rootPath, string? displayName = null, IReadOnlyList<string>? ignorePatterns = null) =>
            SetWorkspace(rootPath, WorkspaceKind.Local, null, displayName, ignorePatterns);

        public void SetWorkspace(
            string? rootPath,
            WorkspaceKind kind,
            string? workspaceId,
            string? displayName = null,
            IReadOnlyList<string>? ignorePatterns = null)
        {
            RootPath = rootPath;
            Kind = kind;
            WorkspaceId = workspaceId;
            DisplayName = displayName;
            IgnorePatterns = ignorePatterns ?? Array.Empty<string>();
        }
    }

    internal sealed class DisconnectedSshClient : ISshWorkspaceClient
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
        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SSH not connected");
        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
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

    internal sealed class StubImageAttachmentStore : IImageAttachmentStore
    {
        public ImageAttachment SaveFromFile(string sessionId, string sourcePath) =>
            new(Path.GetFileName(sourcePath), "image/png", LocalPath: sourcePath);

        public ImageAttachment SaveBytes(string sessionId, string fileName, string mimeType, byte[] bytes) =>
            new(fileName, mimeType, LocalPath: Path.Combine(Path.GetTempPath(), fileName));
    }

    internal sealed class StubSkillCatalog(IReadOnlyList<AgentSkill> skills) : IAgentSkillCatalog
    {
        public IReadOnlyList<AgentSkill> Skills { get; } = skills;

        public AgentSkill? GetSkill(string name) =>
            Skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.Ordinal));

        public AgentSkill? GetSkillById(string skillId) => GetSkill(skillId);

        public void Reload()
        {
        }
    }

    internal sealed class ConnectedMcpRegistry(string serverName, params string[] toolNames) : IMcpRegistry
    {
        private readonly IReadOnlyList<McpCatalogEntry> _catalog = toolNames
            .Select(tool => new McpCatalogEntry(
                serverName,
                tool,
                McpToolNameCodec.Encode(serverName, tool),
                $"{tool} description",
                "{}"))
            .ToArray();

        public int CatalogVersion => 0;
        public int CatalogCount => _catalog.Count;
        public int CatalogSchemaCharCount => _catalog.Sum(entry =>
            entry.Description.Length + entry.InputSchemaJson.Length + entry.EncodedName.Length);

        public IReadOnlyList<McpCatalogEntry> ListCatalogEntries() => _catalog;

        public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(
            string query,
            int topK,
            double minScore,
            string? serverName = null) =>
            McpSearchIndex.Search(_catalog, query, topK, minScore);

        public IReadOnlyList<McpServerStatus> GetStatuses() =>
        [
            new McpServerStatus(
                serverName,
                McpConnectionState.Connected,
                "stdio",
                toolNames.Select(tool => new McpTool(tool, $"{tool} description", "{}")).ToArray())
        ];

        public IReadOnlyList<ToolDefinition> ListToolDefinitions() =>
            _catalog.Select(entry => new ToolDefinition(
                entry.EncodedName,
                entry.Description,
                ToolSchema.FromMcp(entry.InputSchemaJson),
                Source: "mcp")).ToArray();

        public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default, Action? onStatusesChanged = null) =>
            Task.CompletedTask;

        public Task ReconnectAsync(string serverName, IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default, Action? onStatusesChanged = null) =>
            Task.CompletedTask;

        public Task<ToolResult> InvokeAsync(
            string serverName,
            string toolName,
            ToolCallArguments args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
