using System.IO;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Mcp;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services;

public sealed class ComposerAtCompletionService
{
    private const int MaxCompletionItems = 30;
    private const int MaxIndexedFiles = 4000;
    private const double McpSearchMinScore = 0.1;

    private readonly IMcpRegistry _mcpRegistry;
    private readonly IComposerSlashCommandRegistry _slashRegistry;
    private readonly object _fileIndexStateLock = new();
    private volatile IReadOnlyList<AtCompletionItemViewModel> _fileSnapshot = Array.Empty<AtCompletionItemViewModel>();
    private volatile IReadOnlyList<AtCompletionItemViewModel> _skillSnapshot = Array.Empty<AtCompletionItemViewModel>();
    private volatile IReadOnlyList<AtCompletionItemViewModel> _mcpSnapshot = Array.Empty<AtCompletionItemViewModel>();
    private int _buildGeneration;
    private string? _indexedWorkspace;
    private volatile bool _slashSourcesInitialized;
    private volatile bool _fileIndexInitialized;
    private volatile bool _fileIndexBuildInFlight;

    public ComposerAtCompletionService(IMcpRegistry mcpRegistry, IComposerSlashCommandRegistry slashRegistry)
    {
        _mcpRegistry = mcpRegistry;
        _slashRegistry = slashRegistry;
    }

    public event Action? SourcesUpdated;

    public void RefreshSources(
        IAgentSkillCatalog skillCatalog,
        AppSettings settings,
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns,
        bool reloadSkills = false)
    {
        if (reloadSkills)
        {
            skillCatalog.Reload();
        }

        _skillSnapshot = BuildSkillIndex(skillCatalog, settings);
        _mcpSnapshot = BuildMcpToolIndex(settings);
        _slashSourcesInitialized = true;

        if (string.IsNullOrWhiteSpace(activeWorkspace) || !Directory.Exists(activeWorkspace))
        {
            lock (_fileIndexStateLock)
            {
                Interlocked.Increment(ref _buildGeneration);
                _fileSnapshot = Array.Empty<AtCompletionItemViewModel>();
                _indexedWorkspace = null;
                _fileIndexInitialized = true;
                _fileIndexBuildInFlight = false;
            }

            RaiseSourcesUpdated();
            return;
        }

        var root = Path.GetFullPath(activeWorkspace);
        int? generation = null;
        lock (_fileIndexStateLock)
        {
            if (!string.Equals(_indexedWorkspace, root, StringComparison.OrdinalIgnoreCase)
                || (_fileSnapshot.Count == 0 && !_fileIndexBuildInFlight))
            {
                _indexedWorkspace = root;
                _fileSnapshot = Array.Empty<AtCompletionItemViewModel>();
                _fileIndexInitialized = false;
                _fileIndexBuildInFlight = true;
                generation = Interlocked.Increment(ref _buildGeneration);
            }
        }

        if (generation is not null)
        {
            var patterns = ignorePatterns.ToArray();
            _ = Task.Run(() => BuildFileIndex(root, patterns, generation.Value));
        }

        RaiseSourcesUpdated();
    }

    public void EnsureFileIndexBuilt(
        IAgentSkillCatalog skillCatalog,
        AppSettings settings,
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns)
    {
        if (!_slashSourcesInitialized)
        {
            RefreshSources(skillCatalog, settings, activeWorkspace, ignorePatterns);
            return;
        }

        if (string.IsNullOrWhiteSpace(activeWorkspace) || !Directory.Exists(activeWorkspace))
        {
            bool needsRefresh;
            lock (_fileIndexStateLock)
            {
                needsRefresh = !_fileIndexInitialized || _indexedWorkspace is not null;
            }

            if (needsRefresh)
            {
                RefreshSources(skillCatalog, settings, activeWorkspace, ignorePatterns);
            }

            return;
        }

        var root = Path.GetFullPath(activeWorkspace);
        bool shouldRefresh;
        lock (_fileIndexStateLock)
        {
            var isCurrentWorkspace = string.Equals(_indexedWorkspace, root, StringComparison.OrdinalIgnoreCase);
            shouldRefresh = !isCurrentWorkspace || (!_fileIndexInitialized && !_fileIndexBuildInFlight);
        }

        if (shouldRefresh)
        {
            RefreshSources(skillCatalog, settings, activeWorkspace, ignorePatterns);
        }
    }

    public void EnsureSlashSourcesBuilt(
        IAgentSkillCatalog skillCatalog,
        AppSettings settings,
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns)
    {
        if (!_slashSourcesInitialized)
        {
            RefreshSources(skillCatalog, settings, activeWorkspace, ignorePatterns);
        }
    }

    public IReadOnlyList<AtCompletionItemViewModel> FilterMatches(ComposerCompletionTrigger trigger, string query) =>
        trigger switch
        {
            ComposerCompletionTrigger.At => FilterItems(_fileSnapshot, query),
            ComposerCompletionTrigger.Slash => FilterSlashItems(query),
            _ => Array.Empty<AtCompletionItemViewModel>()
        };

    public static bool TryGetQuery(
        string text,
        int caretIndex,
        IComposerSlashCommandRegistry slashRegistry,
        out ComposerCompletionTrigger trigger,
        out string query)
    {
        trigger = ComposerCompletionTrigger.None;
        query = string.Empty;
        if (!ComposerCompletionQuery.TryGetActiveQuery(
                text,
                caretIndex,
                slashRegistry,
                out trigger,
                out _,
                out _,
                out query))
        {
            return false;
        }

        return trigger != ComposerCompletionTrigger.None;
    }

    public static string FormatReplacement(AtCompletionItemViewModel item)
    {
        var replacement = item.InsertText;
        return replacement.EndsWith(' ') ? replacement : replacement + " ";
    }

    private IReadOnlyList<AtCompletionItemViewModel> FilterSlashItems(string query)
    {
        var commandItems = BuildCommandItems(query);
        var skillItems = FilterItems(_skillSnapshot, query);
        var mcpItems = FilterMcpItems(query);

        return commandItems
            .Concat(skillItems)
            .Concat(mcpItems)
            .OrderBy(item => Rank(item.MatchText, query))
            .ThenBy(item => item.Kind == ComposerCompletionItemKind.SlashCommand ? 0 : 1)
            .ThenBy(item => item.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompletionItems)
            .ToArray();
    }

    private IReadOnlyList<AtCompletionItemViewModel> BuildCommandItems(string query)
    {
        var commands = string.IsNullOrWhiteSpace(query)
            ? _slashRegistry.All
            : _slashRegistry.MatchPrefix(query, MaxCompletionItems);

        return commands
            .Select(command => new AtCompletionItemViewModel(
                Type: "命令",
                PrimaryText: command.Name,
                SecondaryText: command.Description,
                InsertText: $"/{command.Name}",
                MatchText: $"{command.Name} {command.Description}",
                Kind: ComposerCompletionItemKind.SlashCommand,
                SlashCommandName: command.Name))
            .ToArray();
    }

    private static IReadOnlyList<AtCompletionItemViewModel> FilterItems(
        IEnumerable<AtCompletionItemViewModel> source,
        string query) =>
        source
            .Where(item => MatchesQuery(item.MatchText, query))
            .OrderBy(item => Rank(item.MatchText, query))
            .ThenBy(item => item.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompletionItems)
            .ToArray();

    private IReadOnlyList<AtCompletionItemViewModel> FilterMcpItems(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _mcpSnapshot;
        }

        var allowedEncodedNames = _mcpSnapshot
            .Select(GetMcpEncodedName)
            .ToHashSet(StringComparer.Ordinal);

        var fromSearch = _mcpRegistry
            .SearchCatalog(query, MaxCompletionItems, McpSearchMinScore)
            .Select(result => result.Entry.EncodedName)
            .Where(allowedEncodedNames.Contains)
            .ToHashSet(StringComparer.Ordinal);

        var searched = _mcpSnapshot
            .Where(item => fromSearch.Contains(GetMcpEncodedName(item)))
            .ToArray();

        return searched.Length > 0 ? searched : FilterItems(_mcpSnapshot, query);
    }

    private static string GetMcpEncodedName(AtCompletionItemViewModel item) =>
        item.InsertText.StartsWith("//mcp:", StringComparison.Ordinal)
            ? item.InsertText["//mcp:".Length..]
            : item.InsertText;

    private IReadOnlyList<AtCompletionItemViewModel> BuildSkillIndex(IAgentSkillCatalog skillCatalog, AppSettings settings)
    {
        var items = new List<AtCompletionItemViewModel>();
        foreach (var skill in SkillFilter.GetEnabledSkills(skillCatalog, settings))
        {
            items.Add(new AtCompletionItemViewModel(
                Type: "技能",
                PrimaryText: skill.Name,
                SecondaryText: skill.SkillId,
                InsertText: $"//skill:{skill.SkillId}",
                MatchText: $"{skill.Name} {skill.SkillId}",
                Kind: ComposerCompletionItemKind.Skill));
        }

        return items;
    }

    private IReadOnlyList<AtCompletionItemViewModel> BuildMcpToolIndex(AppSettings settings)
    {
        var items = new List<AtCompletionItemViewModel>();
        var statuses = _mcpRegistry.GetStatuses().ToDictionary(status => status.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var server in settings.McpServers.Where(server => server.Enabled))
        {
            if (!statuses.TryGetValue(server.Name, out var status)
                || status.State != McpConnectionState.Connected)
            {
                continue;
            }

            foreach (var tool in status.Tools)
            {
                var encoded = McpToolNameCodec.Encode(server.Name, tool.Name);
                items.Add(new AtCompletionItemViewModel(
                    Type: "MCP",
                    PrimaryText: tool.Name,
                    SecondaryText: server.Name,
                    InsertText: $"//mcp:{encoded}",
                    MatchText: $"{server.Name} {tool.Name} {encoded} {tool.Description}",
                    Kind: ComposerCompletionItemKind.Mcp));
            }
        }

        return items
            .OrderBy(item => item.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void BuildFileIndex(string root, IReadOnlyList<string> ignorePatterns, int generation)
    {
        var candidates = new List<(AtCompletionItemViewModel Item, int Depth, DateTime LastWriteUtc)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (generation != _buildGeneration)
                {
                    return;
                }

                if (WorkspacePathFilter.ShouldIgnorePath(path, ignorePatterns))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var depth = relative.Count(c => c == '/');
                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    lastWriteUtc = DateTime.MinValue;
                }

                candidates.Add((
                    new AtCompletionItemViewModel(
                        Type: "文件",
                        PrimaryText: Path.GetFileName(path),
                        SecondaryText: relative,
                        InsertText: $"@{relative}",
                        MatchText: $"{relative} {Path.GetFileName(path)}",
                        Kind: ComposerCompletionItemKind.File),
                    depth,
                    lastWriteUtc));

                if (candidates.Count > MaxIndexedFiles * 2)
                {
                    TrimCandidateBuffer(candidates);
                }
            }
        }
        catch
        {
            // Keep whatever was indexed successfully.
        }

        var ordered = candidates
            .OrderBy(item => item.Depth)
            .ThenByDescending(item => item.LastWriteUtc)
            .Take(MaxIndexedFiles)
            .Select(item => item.Item)
            .ToArray();

        lock (_fileIndexStateLock)
        {
            if (generation != _buildGeneration)
            {
                return;
            }

            _fileSnapshot = ordered;
            _fileIndexInitialized = true;
            _fileIndexBuildInFlight = false;
        }

        RaiseSourcesUpdated();
    }

    private void RaiseSourcesUpdated()
    {
        try
        {
            SourcesUpdated?.Invoke();
        }
        catch
        {
            // UI handlers must not break indexing.
        }
    }

    private static void TrimCandidateBuffer(List<(AtCompletionItemViewModel Item, int Depth, DateTime LastWriteUtc)> candidates)
    {
        var trimmed = candidates
            .OrderBy(item => item.Depth)
            .ThenByDescending(item => item.LastWriteUtc)
            .Take(MaxIndexedFiles)
            .ToList();
        candidates.Clear();
        candidates.AddRange(trimmed);
    }

    private static bool MatchesQuery(string haystack, string query) =>
        string.IsNullOrWhiteSpace(query) || haystack.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static int Rank(string haystack, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        return haystack.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
