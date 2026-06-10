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
        CancellationToken cancellationToken = default) =>
        await JsonConfigFileService.LoadAsync<List<SkillSettings>>(GetPath(paths), cancellationToken) ?? [];

    public static Task SaveSkillsAsync(
        IAppPathProvider paths,
        IEnumerable<SkillSettings> skills,
        CancellationToken cancellationToken = default) =>
        JsonConfigFileService.SaveAsync(GetPath(paths), skills.ToList(), cancellationToken);

    private static string? ReadSkillsJson(IAppPathProvider paths)
    {
        var path = GetPath(paths);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static List<SkillSettings> ParseSkillsJson(string? json) =>
        JsonConfigFileService.Deserialize<List<SkillSettings>>(json) ?? [];
}
