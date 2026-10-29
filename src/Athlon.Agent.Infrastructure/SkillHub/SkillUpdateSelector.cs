using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure.SkillHub;

/// <summary>
/// Picks hub skills that already have a local manifest and are behind the remote commit.
/// </summary>
internal static class SkillUpdateSelector
{
    internal sealed record LocalSkillRevision(string SkillId, long CommitSeq, IReadOnlySet<string> MatchKeys);

    public static IReadOnlyList<LocalSkillRevision> ReadLocalRevisions(string manifestDirectory, string hubSkillsPath)
    {
        var bySkillId = new Dictionary<string, MutableRevision>(StringComparer.Ordinal);
        if (Directory.Exists(manifestDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(manifestDirectory, "*.json"))
            {
                string text;
                try
                {
                    text = SkillIntegrity.ReadUtf8(file);
                }
                catch (IOException)
                {
                    continue;
                }

                if (!SkillManifest.TryParse(text, out var manifest)
                    || manifest is null
                    || string.IsNullOrWhiteSpace(manifest.SkillId))
                {
                    continue;
                }

                var skillId = manifest.SkillId.Trim();
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { skillId };
                bySkillId[skillId] = new MutableRevision(skillId, manifest.CommitSeq, keys);
            }
        }

        if (Directory.Exists(hubSkillsPath))
        {
            foreach (var dir in Directory.EnumerateDirectories(hubSkillsPath))
            {
                var name = SkillFileSystemHelper.TryReadSkillName(dir);
                if (string.IsNullOrWhiteSpace(name)
                    || !bySkillId.TryGetValue(name.Trim(), out var revision))
                {
                    continue;
                }

                var folderName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(folderName))
                {
                    revision.MatchKeys.Add(folderName);
                }
            }
        }

        return bySkillId.Values
            .Select(static revision => new LocalSkillRevision(revision.SkillId, revision.CommitSeq, revision.MatchKeys))
            .ToArray();
    }

    public static IReadOnlyList<RemoteSkillDto> Select(
        IReadOnlyList<LocalSkillRevision> local,
        IReadOnlyList<RemoteSkillDto> remote)
    {
        if (local.Count == 0 || remote.Count == 0)
        {
            return [];
        }

        var selected = new List<RemoteSkillDto>();
        foreach (var skill in remote)
        {
            var matched = Match(local, skill);
            if (matched is not null && NeedsUpdate(matched, skill))
            {
                selected.Add(skill);
            }
        }

        return selected;
    }

    private static bool NeedsUpdate(LocalSkillRevision local, RemoteSkillDto remote) =>
        remote.CommitSeq <= 0 || remote.CommitSeq > local.CommitSeq;

    private static LocalSkillRevision? Match(IReadOnlyList<LocalSkillRevision> local, RemoteSkillDto remote)
    {
        var keys = RemoteMatchKeys(remote);
        if (keys.Count == 0)
        {
            return null;
        }

        foreach (var revision in local)
        {
            foreach (var key in keys)
            {
                if (string.Equals(key, revision.SkillId, StringComparison.OrdinalIgnoreCase))
                {
                    return revision;
                }
            }
        }

        foreach (var revision in local)
        {
            foreach (var key in keys)
            {
                if (revision.MatchKeys.Contains(key))
                {
                    return revision;
                }
            }
        }

        return null;
    }

    private static List<string> RemoteMatchKeys(RemoteSkillDto item)
    {
        var keys = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(item.EnglishName))
        {
            var englishName = item.EnglishName.Trim();
            keys.Add(englishName);
            try
            {
                var sanitized = SkillPackageInstaller.SanitizeFolderName(englishName);
                if (!keys.Contains(sanitized, StringComparer.OrdinalIgnoreCase))
                {
                    keys.Add(sanitized);
                }
            }
            catch (ArgumentException)
            {
                // ignore names that cannot be a folder
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            keys.Add(item.Name.Trim());
        }

        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            keys.Add(item.Id.Trim());
        }

        return keys;
    }

    private sealed class MutableRevision(string skillId, long commitSeq, HashSet<string> matchKeys)
    {
        public string SkillId { get; } = skillId;

        public long CommitSeq { get; } = commitSeq;

        public HashSet<string> MatchKeys { get; } = matchKeys;
    }
}
