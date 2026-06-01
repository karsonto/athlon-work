using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public static class SkillConfigFileService
{
    public const string FileName = "skills.json";

    public static string GetPath(IAppPathProvider paths) => Path.Combine(paths.ConfigPath, FileName);

    public static List<SkillSettings> LoadSkills(IAppPathProvider paths) =>
        ParseSkillsJson(ReadSkillsJson(paths));

    public static async Task<List<SkillSettings>> LoadSkillsAsync(
        IAppPathProvider paths,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(paths);
        if (!File.Exists(path))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseSkillsJson(json);
    }

    public static Task SaveSkillsAsync(
        IAppPathProvider paths,
        IEnumerable<SkillSettings> skills,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.ConfigPath);
        var json = JsonSerializer.Serialize(skills.ToList(), JsonFileStore.Options);
        return AtomicFile.WriteAllTextAsync(GetPath(paths), json, cancellationToken);
    }

    private static string? ReadSkillsJson(IAppPathProvider paths)
    {
        var path = GetPath(paths);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static List<SkillSettings> ParseSkillsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SkillSettings>>(json, JsonFileStore.Options) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
