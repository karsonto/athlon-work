using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Core;

/// <summary>
/// Expands @skill:skillId references in user composer text before sending to the agent.
/// </summary>
public static partial class SkillComposerExpander
{
    [GeneratedRegex(@"@skill:([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SkillReferencePattern();

    public static string Expand(string userInput, IReadOnlyList<AvailableSkillInfo> availableSkills)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return userInput;
        }

        var knownIds = availableSkills
            .Select(skill => skill.SkillId)
            .ToHashSet(StringComparer.Ordinal);

        var matches = SkillReferencePattern().Matches(userInput);
        if (matches.Count == 0)
        {
            return userInput;
        }

        var blocks = new List<string>();
        var warnings = new List<string>();

        foreach (Match match in matches)
        {
            var skillId = match.Groups[1].Value;
            if (knownIds.Contains(skillId))
            {
                blocks.Add(
                    $"[Skill reference: {skillId}]{Environment.NewLine}"
                    + $"Use load_skill_through_path(skillId=\"{skillId}\", path=\"SKILL.md\") "
                    + "to load full instructions before proceeding.");
            }
            else
            {
                warnings.Add($"Unknown skill-id '{skillId}' in @skill reference; install or enable the skill first.");
            }
        }

        var builder = new StringBuilder();
        if (blocks.Count > 0)
        {
            builder.AppendLine(string.Join(Environment.NewLine + Environment.NewLine, blocks.Distinct(StringComparer.Ordinal)));
            builder.AppendLine();
        }

        builder.Append(userInput);

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(string.Join(Environment.NewLine, warnings.Distinct(StringComparer.Ordinal)));
        }

        return builder.ToString();
    }
}
