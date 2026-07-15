using System.IO;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.ViewModels;

public static class AgentRecordGrouping
{
    public const string NoWorkspaceKey = "no-workspace";

    public static IReadOnlyList<AgentRecordGroupViewModel> Build(
        IReadOnlyList<SessionIndexEntry> entries,
        string activeSessionId,
        Func<string, bool> isRunning,
        Action<string>? stopSession,
        IReadOnlySet<string>? previouslyExpandedKeys = null)
    {
        var buckets = new Dictionary<string, (string Title, string? WorkspacePath, List<SessionHistoryItemViewModel> Items)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var item = new SessionHistoryItemViewModel(
                entry,
                entry.Id == activeSessionId,
                isRunning(entry.Id),
                stopSession);

            var key = ResolveRepositoryKey(entry.ActiveWorkspace);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = (
                    ResolveRepositoryTitle(entry.ActiveWorkspace),
                    NormalizeWorkspacePath(entry.ActiveWorkspace),
                    []);
                buckets[key] = bucket;
            }

            bucket.Items.Add(item);
        }

        var ordered = buckets
            .OrderBy(pair => pair.Key == NoWorkspaceKey ? 0 : 1)
            .ThenByDescending(pair => pair.Value.Items.Max(item => item.UpdatedAt))
            .ThenBy(pair => pair.Value.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasActive = ordered.Any(pair => pair.Value.Items.Any(item => item.IsActive));
        var preserveExpand = previouslyExpandedKeys is { Count: > 0 };
        var result = new List<AgentRecordGroupViewModel>(ordered.Count);
        var expandedFirstFallback = false;

        foreach (var (key, bucket) in ordered)
        {
            var containsActive = bucket.Items.Any(item => item.IsActive);
            var expanded = preserveExpand
                ? previouslyExpandedKeys!.Contains(key)
                : containsActive || (!hasActive && !expandedFirstFallback);

            if (!preserveExpand && !hasActive && !expandedFirstFallback && expanded)
            {
                expandedFirstFallback = true;
            }

            var group = new AgentRecordGroupViewModel(
                key,
                bucket.Title,
                isExpandedByDefault: expanded,
                workspacePath: bucket.WorkspacePath);
            foreach (var item in bucket.Items.OrderByDescending(i => i.UpdatedAt))
            {
                group.Items.Add(item);
            }

            result.Add(group);
        }

        return result;
    }

    public static string ResolveRepositoryKey(string? activeWorkspace)
    {
        var normalized = NormalizeWorkspacePath(activeWorkspace);
        return normalized is null ? NoWorkspaceKey : normalized;
    }

    public static string ResolveRepositoryTitle(string? activeWorkspace)
    {
        var normalized = NormalizeWorkspacePath(activeWorkspace);
        if (normalized is null)
        {
            return Strings.Get("RecordGroup_NoWorkspace");
        }

        var name = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? normalized : name;
    }

    private static string? NormalizeWorkspacePath(string? activeWorkspace)
    {
        if (string.IsNullOrWhiteSpace(activeWorkspace))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(activeWorkspace)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return activeWorkspace.Trim();
        }
    }
}
