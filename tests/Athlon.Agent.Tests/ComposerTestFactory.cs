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
        IComposerSlashCommandRegistry? slashRegistry = null) =>
        new(mcpRegistry ?? new TestMcpRegistry(), slashRegistry ?? CreateSlashRegistry());

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

        public Task<ToolResult> InvokeAsync(
            string serverName,
            string toolName,
            ToolCallArguments args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
