using System.IO;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services;

public sealed class ComposerAtCompletionService
{
    private const int MaxCompletionItems = 30;
    private const int MaxIndexedFiles = 4000;

    private volatile IReadOnlyList<AtCompletionItemViewModel> _fileSnapshot = Array.Empty<AtCompletionItemViewModel>();
    private volatile IReadOnlyList<AtCompletionItemViewModel> _skillSnapshot = Array.Empty<AtCompletionItemViewModel>();
    private int _buildGeneration;
    private string? _indexedWorkspace;

    public bool IsFileIndexEmpty => _fileSnapshot.Count == 0;

    public void RefreshSources(
        IAgentSkillCatalog skillCatalog,
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns,
        bool reloadSkills = false)
    {
        if (reloadSkills)
        {
            skillCatalog.Reload();
        }

        _skillSnapshot = BuildSkillIndex(skillCatalog);

        if (string.IsNullOrWhiteSpace(activeWorkspace) || !Directory.Exists(activeWorkspace))
        {
            _fileSnapshot = Array.Empty<AtCompletionItemViewModel>();
            _indexedWorkspace = null;
            return;
        }

        var root = Path.GetFullPath(activeWorkspace);
        _indexedWorkspace = root;
        var generation = Interlocked.Increment(ref _buildGeneration);
        var patterns = ignorePatterns.ToArray();
        _ = Task.Run(() => BuildFileIndex(root, patterns, generation));
    }

    public void EnsureFileIndexBuilt(
        IAgentSkillCatalog skillCatalog,
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns)
    {
        if (!IsFileIndexEmpty)
        {
            return;
        }

        RefreshSources(skillCatalog, activeWorkspace, ignorePatterns);
    }

    public IReadOnlyList<AtCompletionItemViewModel> FilterMatches(string query) =>
        _fileSnapshot
            .Concat(_skillSnapshot)
            .Where(item => MatchesQuery(item.MatchText, query))
            .OrderBy(item => Rank(item.MatchText, query))
            .ThenBy(item => item.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompletionItems)
            .ToArray();

    public static bool TryGetQuery(string text, int caretIndex, out string query)
    {
        query = string.Empty;
        if (!ComposerCompletionQuery.TryGetAtQuerySpan(text, caretIndex, out var atStart, out var atEndExclusive))
        {
            return false;
        }

        query = text[(atStart + 1)..atEndExclusive];
        return true;
    }

    public static string FormatReplacement(AtCompletionItemViewModel item)
    {
        var replacement = item.InsertText;
        return replacement.EndsWith(' ') ? replacement : replacement + " ";
    }

    private static IReadOnlyList<AtCompletionItemViewModel> BuildSkillIndex(IAgentSkillCatalog skillCatalog)
    {
        var items = new List<AtCompletionItemViewModel>();
        foreach (var skill in skillCatalog.Skills)
        {
            items.Add(new AtCompletionItemViewModel(
                Type: "技能",
                PrimaryText: skill.Name,
                SecondaryText: skill.SkillId,
                InsertText: $"@skill:{skill.SkillId}",
                MatchText: $"{skill.Name} {skill.SkillId}"));
        }

        return items;
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
                        MatchText: $"{relative} {Path.GetFileName(path)}"),
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

        if (generation != _buildGeneration)
        {
            return;
        }

        var ordered = candidates
            .OrderBy(item => item.Depth)
            .ThenByDescending(item => item.LastWriteUtc)
            .Take(MaxIndexedFiles)
            .Select(item => item.Item)
            .ToArray();

        _fileSnapshot = ordered;
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
