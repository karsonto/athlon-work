using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// On-disk layout: only <c>sessions/{id}/session.json</c> (direct children) are main chats.
/// Sub-agent transcripts live under <c>sessions/{parent}/subagents/default/{id}/</c>.
/// </summary>
internal static class SessionDirectoryLayout
{
    public const string SubAgentsFolder = "subagents";
    public const string SubAgentKind = "default";

    public static bool IsTopLevelSessionDirectory(string sessionsPath, string sessionDirectory)
    {
        var normalizedRoot = Path.GetFullPath(sessionsPath);
        var normalizedDir = Path.GetFullPath(sessionDirectory);
        var parent = Path.GetDirectoryName(normalizedDir);
        return parent is not null
            && string.Equals(parent, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static HashSet<string> CollectNestedSubAgentSessionIds(string sessionsPath)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(sessionsPath))
        {
            return ids;
        }

        foreach (var parentDir in Directory.EnumerateDirectories(sessionsPath))
        {
            var subAgentsRoot = Path.Combine(parentDir, SubAgentsFolder, SubAgentKind);
            if (!Directory.Exists(subAgentsRoot))
            {
                continue;
            }

            foreach (var nestedDir in Directory.EnumerateDirectories(subAgentsRoot))
            {
                var subSessionId = Path.GetFileName(nestedDir);
                if (!string.IsNullOrWhiteSpace(subSessionId))
                {
                    ids.Add(subSessionId);
                }
            }
        }

        return ids;
    }

    public static string? TryFindNestedSubAgentDirectory(string sessionsPath, string subSessionId)
    {
        if (!Directory.Exists(sessionsPath))
        {
            return null;
        }

        foreach (var parentDir in Directory.EnumerateDirectories(sessionsPath))
        {
            var nested = Path.Combine(parentDir, SubAgentsFolder, SubAgentKind, subSessionId);
            if (Directory.Exists(nested))
            {
                return nested;
            }
        }

        return null;
    }

    public static bool IsNestedSubAgentSessionId(string sessionsPath, string sessionId) =>
        TryFindNestedSubAgentDirectory(sessionsPath, sessionId) is not null;

    public static IEnumerable<string> EnumerateTopLevelSessionJsonPaths(string sessionsPath)
    {
        if (!Directory.Exists(sessionsPath))
        {
            yield break;
        }

        foreach (var sessionDir in Directory.EnumerateDirectories(sessionsPath))
        {
            var sessionJson = Path.Combine(sessionDir, "session.json");
            if (File.Exists(sessionJson))
            {
                yield return sessionJson;
            }
        }
    }

    public static bool IsEligibleForSessionMenu(
        string sessionsPath,
        SessionIndexEntry entry,
        ISet<string>? nestedSubAgentSessionIds = null) =>
        IsTopLevelSessionDirectory(sessionsPath, entry.Path)
        && !(nestedSubAgentSessionIds ?? CollectNestedSubAgentSessionIds(sessionsPath)).Contains(entry.Id);
}
