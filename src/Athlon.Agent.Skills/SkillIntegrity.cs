using System.Text;

namespace Athlon.Agent.Skills;

/// <summary>
/// Checks a skill directory against a canonical manifest before it can enter the prompt.
/// </summary>
public static class SkillIntegrity
{
    public static bool TryVerify(string skillDirectory, string manifestText, long anchorCommitSeq, string expectedSkillId, out string error)
    {
        error = "";
        if (!SkillManifest.TryParse(manifestText, out var manifest) || manifest is null)
        {
            error = "manifest is invalid";
            return false;
        }

        if (!string.Equals(manifest.SkillId, expectedSkillId, StringComparison.Ordinal))
        {
            error = "manifest skillId does not match the skill";
            return false;
        }

        if (manifest.CommitSeq < 1 || (anchorCommitSeq > 0 && manifest.CommitSeq < anchorCommitSeq))
        {
            error = "commitSeq is older than the anchored version";
            return false;
        }

        try
        {
            var files = ListFiles(skillDirectory);
            var canonical = SkillManifest.Canonical(manifest.SkillId, manifest.CommitSeq, files);
            if (!string.Equals(canonical, manifestText, StringComparison.Ordinal))
            {
                error = "skill files do not match the manifest";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }

    public static IReadOnlyList<SkillManifest.FileEntry> ListFiles(string skillDirectory)
    {
        var root = Path.GetFullPath(skillDirectory);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var entries = new List<SkillManifest.FileEntry>();
        if (!Directory.Exists(root))
        {
            return entries;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("skill file escapes its directory");
            }

            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            SkillManifest.ValidateRelativePath(relative);
            var info = new FileInfo(full);
            entries.Add(new SkillManifest.FileEntry(relative, info.Length, SkillManifest.Sha256File(full)));
        }

        return entries;
    }

    public static string ReadUtf8(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
