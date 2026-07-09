using System.IO;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Prompt;
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
    private volatile bool _fileIndexBuildInFlight;

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
        RaiseSourcesUpdated();

        if (string.IsNullOrWhiteSpace(activeWorkspace) || !Directory.Exists(activeWorkspace))
        {
            _fileSnapshot = Array.Empty<AtCompletionItemViewModel>();
            _indexedWorkspace = null;
            _fileIndexBuildInFlight = false;
            return;
        }

        var root = Path.GetFullPath(activeWorkspace);
        if (string.Equals(_indexedWorkspace, root, StringComparison.OrdinalIgnoreCase)
            && (_fileSnapshot.Count > 0 || _fileIndexBuildInFlight))
        {
            return;
        }

        _indexedWorkspace = root;
        _fileSnapshot = Array.Empty<AtCompletionItemViewModel>();
        _fileIndexBuildInFlight = true;
        var generation = Interlocked.Increment(ref _buildGeneration);
        var patterns = ignorePatterns.ToArray();
        _ = Task.Run(() => BuildFileIndex(root, patterns, generation));
    }

    public void EnsureFileIndexBuilt(
        IAgentSkillCatalog skillCatalog,
        AppSettings settings,
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns)
    {
        if (_skillSnapshot.Count == 0)
        {
            RefreshSources(skillCatalog, settings, activeWorkspace, ignorePatterns);
            return;
        }

        if (_fileSnapshot.Count == 0)
        {
            RefreshSources(skillCatalog, settings, activeWorkspace, ignorePatterns);
        }
    }

    public IReadOnlyList<AtCompletionItemViewModel> FilterMatches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            // Snapshots are already ranked (skills, then shallow/recent files). Avoid sorting thousands of items on the UI thread.
            return TakeCompletionItems(_skillSnapshot, _fileSnapshot);
        }

        var matches = new List<AtCompletionItemViewModel>(MaxCompletionItems);
        foreach (var item in _skillSnapshot)
        {
            if (MatchesQuery(item.MatchText, query))
            {
                matches.Add(item);
            }
        }

        foreach (var item in _fileSnapshot)
        {
            if (!MatchesQuery(item.MatchText, query))
            {
                continue;
            }

            matches.Add(item);
            if (matches.Count >= MaxCompletionItems * 4)
            {
                break;
            }
        }

        return matches
            .OrderBy(item => Rank(item.MatchText, query))
            .ThenBy(item => item.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompletionItems)
            .ToArray();
    }

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

    private static IReadOnlyList<AtCompletionItemViewModel> BuildSkillIndex(IAgentSkillCatalog skillCatalog, AppSettings settings)
    {
        var items = new List<AtCompletionItemViewModel>();
        foreach (var skill in SkillFilter.GetEnabledSkills(skillCatalog, settings))
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
            IndexWorkspaceDirectory(root, root, ignorePatterns, generation, candidates);
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
        _fileIndexBuildInFlight = false;
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

    private void IndexWorkspaceDirectory(
        string root,
        string directoryPath,
        IReadOnlyList<string> ignorePatterns,
        int generation,
        List<(AtCompletionItemViewModel Item, int Depth, DateTime LastWriteUtc)> candidates)
    {
        if (generation != _buildGeneration)
        {
            return;
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directoryPath);
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (generation != _buildGeneration)
            {
                return;
            }

            var entryName = Path.GetFileName(entry);
            if (WorkspacePathFilter.ShouldIgnoreEntryName(entryName, ignorePatterns))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                IndexWorkspaceDirectory(root, entry, ignorePatterns, generation, candidates);
                continue;
            }

            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            var depth = relative.Count(c => c == '/');
            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(entry);
            }
            catch
            {
                lastWriteUtc = DateTime.MinValue;
            }

            candidates.Add((
                new AtCompletionItemViewModel(
                    Type: "文件",
                    PrimaryText: entryName,
                    SecondaryText: relative,
                    InsertText: $"@{relative}",
                    MatchText: $"{relative} {entryName}"),
                depth,
                lastWriteUtc));

            if (candidates.Count > MaxIndexedFiles * 2)
            {
                TrimCandidateBuffer(candidates);
            }
        }
    }

    private static IReadOnlyList<AtCompletionItemViewModel> TakeCompletionItems(
        IReadOnlyList<AtCompletionItemViewModel> skills,
        IReadOnlyList<AtCompletionItemViewModel> files)
    {
        if (skills.Count >= MaxCompletionItems)
        {
            return skills.Take(MaxCompletionItems).ToArray();
        }

        var remaining = MaxCompletionItems - skills.Count;
        if (remaining <= 0 || files.Count == 0)
        {
            return skills.ToArray();
        }

        var combined = new AtCompletionItemViewModel[skills.Count + Math.Min(remaining, files.Count)];
        skills.CopyTo(combined, 0);
        for (var i = 0; i < Math.Min(remaining, files.Count); i++)
        {
            combined[skills.Count + i] = files[i];
        }

        return combined;
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
