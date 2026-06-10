using System.IO;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services;

public sealed class ComposerAtCompletionService
{
    private const int MaxCompletionItems = 30;
    private readonly List<AtCompletionItemViewModel> _fileCompletionIndex = new();
    private readonly List<AtCompletionItemViewModel> _skillCompletionIndex = new();

    public void RefreshSources(
        IAgentSkillCatalog skillCatalog,
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns)
    {
        _fileCompletionIndex.Clear();
        _skillCompletionIndex.Clear();

        skillCatalog.Reload();
        foreach (var skill in skillCatalog.Skills)
        {
            _skillCompletionIndex.Add(new AtCompletionItemViewModel(
                Type: "技能",
                PrimaryText: skill.Name,
                SecondaryText: skill.SkillId,
                InsertText: $"@skill:{skill.SkillId}",
                MatchText: $"{skill.Name} {skill.SkillId}"));
        }

        if (string.IsNullOrWhiteSpace(activeWorkspace) || !Directory.Exists(activeWorkspace))
        {
            return;
        }

        var root = Path.GetFullPath(activeWorkspace);
        var ignoredNames = new HashSet<string>(ignorePatterns, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (ignoredNames.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                _fileCompletionIndex.Add(new AtCompletionItemViewModel(
                    Type: "文件",
                    PrimaryText: Path.GetFileName(path),
                    SecondaryText: relative,
                    InsertText: $"@{relative}",
                    MatchText: $"{relative} {Path.GetFileName(path)}"));
            }
        }
        catch
        {
            // Keep whatever was indexed successfully.
        }
    }

    public IReadOnlyList<AtCompletionItemViewModel> FilterMatches(string query) =>
        _fileCompletionIndex
            .Concat(_skillCompletionIndex)
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
