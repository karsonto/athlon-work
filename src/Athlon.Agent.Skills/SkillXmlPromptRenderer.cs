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
        builder.AppendLine("Load resources with the same tool and a relative path (e.g. references/guide.md).");
        builder.AppendLine("Do not use '.', './', absolute paths, or the skills directory root as path.");
        builder.AppendLine("</usage>");
        builder.AppendLine();
        builder.AppendLine("<available_skills>");

        foreach (var skill in skills)
        {
            AppendSkill(builder, skill);
        }

        builder.AppendLine("</available_skills>");
        builder.AppendLine();
    }

    private static void AppendSkill(StringBuilder builder, AgentSkill skill)
    {
        builder.AppendLine("<skill>");
        AppendXmlNode(builder, "name", skill.Name, 1);
        AppendXmlNode(builder, "description", skill.Description, 1);
        AppendXmlNode(builder, "skill-id", skill.SkillId, 1);
        builder.AppendLine("</skill>");
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
