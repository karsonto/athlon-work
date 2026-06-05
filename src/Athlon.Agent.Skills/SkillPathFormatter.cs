using Athlon.Agent.Core;

namespace Athlon.Agent.Skills;

public static class SkillPathFormatter
{
    public static string? FormatFilesRoot(AgentSkill skill) =>
        FormatFilesRoot(skill.SkillDirectory);

    public static string? FormatFilesRoot(string? skillDirectory)
    {
        if (string.IsNullOrWhiteSpace(skillDirectory) || !Directory.Exists(skillDirectory))
        {
            return null;
        }

        return ToolPathNormalizer.ForModel(Path.GetFullPath(skillDirectory));
    }
}
