using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure.SkillHub;

/// <summary>
/// Decides whether a hub skill should be fully downloaded, updated from changed files, or left as-is.
/// </summary>
internal static class SkillInstallClassifier
{
    public const string Use = "use";
    public const string Update = "update";
    public const string Installed = "installed";

    public static string Classify(string? skillDirectory, string? manifestText, long anchorCommitSeq)
    {
        if (string.IsNullOrWhiteSpace(skillDirectory)
            || !SkillFileSystemHelper.HasSkillFile(skillDirectory))
        {
            return Use;
        }

        if (string.IsNullOrWhiteSpace(manifestText)
            || !SkillManifest.TryParse(manifestText, out var manifest)
            || manifest is null)
        {
            return Update;
        }

        if (anchorCommitSeq > 0 && manifest.CommitSeq < anchorCommitSeq)
        {
            return Installed;
        }

        var name = SkillFileSystemHelper.TryReadSkillName(skillDirectory);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Update;
        }

        if (SkillIntegrity.TryVerify(skillDirectory, manifestText, anchorCommitSeq: 0, name, out _))
        {
            return Installed;
        }

        return Update;
    }

    public static string? FindInstalledDirectory(string skillsRoot, RemoteSkillDto skill)
    {
        if (string.IsNullOrWhiteSpace(skillsRoot) || !Directory.Exists(skillsRoot))
        {
            return null;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddKey(keys, skill.EnglishName);
        AddKey(keys, skill.Name);
        AddKey(keys, skill.Id);
        foreach (var key in keys.ToArray())
        {
            try
            {
                AddKey(keys, SkillPackageInstaller.SanitizeFolderName(key));
            }
            catch (ArgumentException)
            {
                // ignore names that cannot be folders
            }
        }

        if (keys.Count == 0)
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(skillsRoot))
        {
            if (!SkillFileSystemHelper.HasSkillFile(dir))
            {
                continue;
            }

            var folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (keys.Contains(folder))
            {
                return dir;
            }

            var name = SkillFileSystemHelper.TryReadSkillName(dir);
            if (!string.IsNullOrWhiteSpace(name) && keys.Contains(name))
            {
                return dir;
            }
        }

        return null;
    }

    private static void AddKey(HashSet<string> keys, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            keys.Add(value.Trim());
        }
    }
}
