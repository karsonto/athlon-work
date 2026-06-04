using System.Text;

namespace Athlon.Agent.Skills;

/// <summary>
/// File-system operations for AgentScope-style skill folders.
/// </summary>
public static class SkillFileSystemHelper
{
    public static AgentSkill LoadSkill(string baseDir, string skillName)
    {
        var skillDir = FindSkillDirectoryByName(baseDir, skillName)
            ?? throw new ArgumentException($"Skill directory does not exist for skill name: {skillName}");

        return LoadSkillFromDirectory(skillDir);
    }

    public static AgentSkill LoadSkillFromDirectory(string skillDir) =>
        LoadSkillFromDirectory(skillDir, loadResourceContents: false);

    public static AgentSkill LoadSkillFromDirectoryWithResources(string skillDir) =>
        LoadSkillFromDirectory(skillDir, loadResourceContents: true);

    private static AgentSkill LoadSkillFromDirectory(string skillDir, bool loadResourceContents)
    {
        if (!Directory.Exists(skillDir))
        {
            throw new ArgumentException($"Skill directory does not exist: {skillDir}");
        }

        var skillFile = Path.Combine(skillDir, SkillUtil.SkillFileName);
        if (!File.Exists(skillFile))
        {
            throw new ArgumentException($"SKILL.md not found in skill directory: {skillDir}");
        }

        var skillMdContent = File.ReadAllText(skillFile, Encoding.UTF8);
        var resourcePaths = ListResourcePaths(skillDir, skillFile);
        if (loadResourceContents)
        {
            var resources = LoadResources(skillDir, skillFile);
            return SkillUtil.CreateFrom(skillMdContent, resources, resourcePaths, skillDir);
        }

        return SkillUtil.CreateFrom(skillMdContent, resourcePaths: resourcePaths, skillDirectory: skillDir);
    }

    public static bool TryReadResourceFile(string skillDirectory, string relativePath, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(skillDirectory) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var skillRoot = Path.GetFullPath(skillDirectory);
        var normalized = relativePath.Trim().Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(skillRoot, normalized));
        if (!fullPath.StartsWith(skillRoot, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            return false;
        }

        var skillFile = Path.Combine(skillRoot, SkillUtil.SkillFileName);
        if (!IsValidResource(fullPath, skillFile))
        {
            return false;
        }

        try
        {
            content = File.ReadAllText(fullPath, Encoding.UTF8);
            return true;
        }
        catch (DecoderFallbackException)
        {
            var bytes = File.ReadAllBytes(fullPath);
            content = "base64:" + Convert.ToBase64String(bytes);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> GetAllSkillNames(string baseDir)
    {
        if (!Directory.Exists(baseDir))
        {
            return Array.Empty<string>();
        }

        var skillNames = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(baseDir))
        {
            if (!HasSkillFile(dir))
            {
                continue;
            }

            var name = ReadSkillName(dir);
            if (!string.IsNullOrWhiteSpace(name))
            {
                skillNames.Add(name);
            }
        }

        skillNames.Sort(StringComparer.Ordinal);
        return skillNames;
    }

    public static IReadOnlyList<AgentSkill> GetAllSkills(string baseDir)
    {
        if (!Directory.Exists(baseDir))
        {
            return Array.Empty<AgentSkill>();
        }

        var skills = new List<AgentSkill>();
        foreach (var dir in Directory.EnumerateDirectories(baseDir))
        {
            if (!HasSkillFile(dir))
            {
                continue;
            }

            try
            {
                skills.Add(LoadSkillFromDirectory(dir));
            }
            catch (Exception)
            {
                // Skip invalid skill folders, matching AgentScope behavior.
            }
        }

        return skills;
    }

    public static bool SkillExists(string baseDir, string skillName) =>
        !string.IsNullOrWhiteSpace(skillName) && FindSkillDirectoryByName(baseDir, skillName) is not null;

    public static IReadOnlyDictionary<string, string> GetSkillNameToFolderMap(string baseDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(baseDir))
        {
            return map;
        }

        foreach (var dir in Directory.EnumerateDirectories(baseDir))
        {
            if (!HasSkillFile(dir))
            {
                continue;
            }

            var name = ReadSkillName(dir);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var folderName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                map[name] = folderName;
            }
        }

        return map;
    }

    public static bool HasSkillFile(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        return File.Exists(Path.Combine(dir, SkillUtil.SkillFileName));
    }

    public static string ValidateAndResolvePath(string baseDir, string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw new ArgumentException("Skill name cannot be null or empty");
        }

        var absoluteBaseDir = Path.GetFullPath(baseDir);
        var resolvedPath = Path.GetFullPath(Path.Combine(absoluteBaseDir, skillName));
        if (!resolvedPath.StartsWith(absoluteBaseDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid skill name: path traversal detected. Skill name '{skillName}' would escape base directory");
        }

        return resolvedPath;
    }

    private static string? FindSkillDirectoryByName(string baseDir, string skillName)
    {
        if (!Directory.Exists(baseDir) || string.IsNullOrWhiteSpace(skillName))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(baseDir))
        {
            if (!HasSkillFile(dir))
            {
                continue;
            }

            var name = ReadSkillName(dir);
            if (string.Equals(name, skillName, StringComparison.Ordinal))
            {
                return dir;
            }
        }

        return null;
    }

    private static string? ReadSkillName(string skillDir)
    {
        var skillFile = Path.Combine(skillDir, SkillUtil.SkillFileName);
        if (!File.Exists(skillFile))
        {
            return null;
        }

        try
        {
            var skillMdContent = File.ReadAllText(skillFile, Encoding.UTF8);
            var parsed = MarkdownSkillParser.Parse(skillMdContent);
            if (!parsed.Metadata.TryGetValue("name", out var nameObject))
            {
                return null;
            }

            var name = Convert.ToString(nameObject);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ListResourcePaths(string skillDir, string skillFile)
    {
        var paths = new List<string>();
        CollectResourcePaths(skillDir, skillDir, skillFile, paths);
        return paths;
    }

    private static void CollectResourcePaths(
        string currentDir,
        string skillDir,
        string skillFile,
        List<string> paths)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(currentDir))
        {
            if (Directory.Exists(entry))
            {
                var dirName = Path.GetFileName(entry);
                if (!dirName.StartsWith(".", StringComparison.Ordinal))
                {
                    CollectResourcePaths(entry, skillDir, skillFile, paths);
                }

                continue;
            }

            if (IsValidResource(entry, skillFile))
            {
                paths.Add(Path.GetRelativePath(skillDir, entry).Replace('\\', '/'));
            }
        }
    }

    private static Dictionary<string, string> LoadResources(string skillDir, string skillFile)
    {
        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectResources(skillDir, skillDir, skillFile, resources);
        return resources;
    }

    private static void CollectResources(
        string currentDir,
        string skillDir,
        string skillFile,
        Dictionary<string, string> resources)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(currentDir))
        {
            if (Directory.Exists(entry))
            {
                var dirName = Path.GetFileName(entry);
                if (!dirName.StartsWith(".", StringComparison.Ordinal))
                {
                    CollectResources(entry, skillDir, skillFile, resources);
                }

                continue;
            }

            if (IsValidResource(entry, skillFile))
            {
                ReadAndPutResource(entry, skillDir, resources);
            }
        }
    }

    private static void ReadAndPutResource(string file, string skillDir, Dictionary<string, string> resources)
    {
        var relativePath = Path.GetRelativePath(skillDir, file).Replace('\\', '/');
        try
        {
            resources[relativePath] = File.ReadAllText(file, Encoding.UTF8);
        }
        catch (DecoderFallbackException)
        {
            var bytes = File.ReadAllBytes(file);
            resources[relativePath] = "base64:" + Convert.ToBase64String(bytes);
        }
        catch (IOException)
        {
            // Skip unreadable resources.
        }
    }

    private static bool IsValidResource(string file, string skillFile)
    {
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(skillFile), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(file);
        if (fileName.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var attributes = File.GetAttributes(file);
        return (attributes & FileAttributes.Hidden) == 0;
    }
}
