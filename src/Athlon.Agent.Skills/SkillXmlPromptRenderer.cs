using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Skills;

public static class SkillXmlPromptRenderer
{
    private static readonly Regex XmlTagNamePattern = new("^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    public static void AppendSkillPrompt(StringBuilder builder, IReadOnlyList<AgentSkill> skills)
    {
        if (skills.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Available Skills");
        builder.AppendLine();
        builder.AppendLine("<usage>");
        builder.AppendLine("Skills provide specialized capabilities. Use them when they match the current task.");
        builder.AppendLine("Load skill: load_skill_through_path(skillId=\"<skill-name>\", path=\"SKILL.md\")");
        builder.AppendLine("Load resources with the same tool and a skill-internal relative path (e.g. references/guide.md).");
        builder.AppendLine("For load_skill_through_path, use only paths inside the skill — not '.', './', absolute paths, or the shared skills install directory.");
        builder.AppendLine("Each <skill> may include <files-root> with the absolute path for shell-executing that skill's scripts.");
        builder.AppendLine("</usage>");
        builder.AppendLine();
        builder.AppendLine("<available_skills>");

        var uniqueSkills = skills
            .GroupBy(skill => skill.SkillId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(skill => skill.SkillId, StringComparer.OrdinalIgnoreCase);
        var hasFilesRoot = false;
        foreach (var skill in uniqueSkills)
        {
            if (AppendSkill(builder, skill))
            {
                hasFilesRoot = true;
            }
        }

        builder.AppendLine("</available_skills>");
        builder.AppendLine();

        if (hasFilesRoot)
        {
            AppendCodeExecutionSection(builder);
        }
    }

    private static bool AppendSkill(StringBuilder builder, AgentSkill skill)
    {
        builder.AppendLine("<skill>");
        AppendXmlNode(builder, "name", skill.Name, 1);
        AppendXmlNode(builder, "description", skill.Description, 1);
        AppendXmlNode(builder, "skill-id", skill.SkillId, 1);

        var filesRoot = SkillPathFormatter.FormatFilesRoot(skill);
        if (filesRoot is not null)
        {
            AppendXmlNode(builder, "files-root", filesRoot, 1);
        }

        builder.AppendLine("</skill>");
        builder.AppendLine();
        return filesRoot is not null;
    }

    private static void AppendCodeExecutionSection(StringBuilder builder)
    {
        builder.AppendLine("## Code Execution");
        builder.AppendLine();
        builder.AppendLine("<code_execution>");
        builder.AppendLine("You have access to execute_command. Each skill in <available_skills> includes a <files-root> element giving the absolute path to that skill's files.");
        builder.AppendLine("Workflow:");
        builder.AppendLine("1. After loading a skill, look at its <files-root> in <available_skills> or the Files root line in the load response.");
        builder.AppendLine("2. List its files:    dir \"<files-root>\"");
        builder.AppendLine("3. Run scripts:       python \"<files-root>/scripts/<script-name>\"");
        builder.AppendLine("4. Always use absolute paths derived from <files-root>; never invent paths.");
        builder.AppendLine("5. execute_command cwd still defaults to the workspace root; run skill scripts via absolute paths in the command, not by switching cwd.");
        builder.AppendLine("6. Quote paths that contain spaces or non-ASCII characters.");
        builder.AppendLine("</code_execution>");
        builder.AppendLine();
    }

    private static void AppendXmlNode(StringBuilder builder, string key, string? value, int indentLevel)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var indent = new string(' ', indentLevel * 2);
        var openTag = XmlTagNamePattern.IsMatch(key) ? $"<{key}>" : $"<entry key=\"{EscapeXml(key)}\">";
        var closeTag = XmlTagNamePattern.IsMatch(key) ? $"</{key}>" : "</entry>";
        builder.Append(indent)
            .Append(openTag)
            .Append(EscapeXml(value))
            .Append(closeTag)
            .AppendLine();
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
