using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure;

public sealed class SkillRuntime(IAgentSkillCatalog catalog, AppSettings settings) : ISkillRuntime
{
    private const string SkillMarkdownPath = "SKILL.md";

    public IReadOnlyList<AvailableSkillInfo> GetSkills() =>
        SkillFilter.GetEnabledSkills(catalog, settings)
            .Select(skill => new AvailableSkillInfo(skill.Name, skill.Description, skill.SkillId))
            .ToArray();

    public string LoadResource(string skillId, string path)
    {
        var skill = ResolveSkill(skillId);
        var normalizedPath = NormalizeResourcePath(path);

        if (string.Equals(normalizedPath, SkillMarkdownPath, StringComparison.Ordinal))
        {
            Activate(skillId);
            return BuildSkillMarkdownResponse(skillId, skill);
        }

        if (!TryResolveResourceContent(skill, normalizedPath, out var resourceContent))
        {
            throw new ArgumentException(
                BuildResourceNotFoundMessage(skillId, normalizedPath, skill.ResourcePaths));
        }

        Activate(skillId);
        return BuildResourceResponse(skillId, normalizedPath, resourceContent);
    }

    public void Activate(string skillId)
    {
        ResolveSkill(skillId);
        SessionSkillActivationScope.CurrentState?.Activate(skillId);
    }

    public bool IsActive(string skillId) =>
        SessionSkillActivationScope.CurrentState?.IsActive(skillId) ?? false;

    private AgentSkill ResolveSkill(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("Missing or empty required parameter: skillId");
        }

        var skill = catalog.GetSkillById(skillId.Trim());
        if (skill is null)
        {
            var available = string.Join(", ", GetSkills().Select(s => s.SkillId));
            throw new ArgumentException(
                $"Skill not found: '{skillId}'. Please check the skill name. Available: {available}");
        }

        if (IsDisabled(skill.Name))
        {
            throw new ArgumentException($"Skill '{skillId}' is disabled in settings.");
        }

        return skill;
    }

    private bool IsDisabled(string skillName) =>
        settings.Skills.Any(skill =>
            !skill.Enabled
            && !string.IsNullOrWhiteSpace(skill.Name)
            && string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase));

    private static bool TryResolveResourceContent(AgentSkill skill, string normalizedPath, out string content)
    {
        if (skill.Resources.TryGetValue(normalizedPath, out content!))
        {
            return true;
        }

        if (skill.SupportsLazyResourceLoad
            && SkillFileSystemHelper.TryReadResourceFile(skill.SkillDirectory!, normalizedPath, out content))
        {
            return true;
        }

        content = string.Empty;
        return false;
    }

    private static string NormalizeResourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Missing or empty required parameter: path");
        }

        var trimmed = path.Trim().Replace('\\', '/');
        if (trimmed is "." or "./" or ".." or "/"
            || trimmed.StartsWith("./", StringComparison.Ordinal)
            || trimmed.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(trimmed))
        {
            throw new ArgumentException(
                "Invalid path: use a relative resource path such as 'SKILL.md' or 'references/guide.md'.");
        }

        return trimmed;
    }

    private static string BuildSkillMarkdownResponse(string skillId, AgentSkill skill)
    {
        var result = new StringBuilder();
        result.AppendLine($"Successfully loaded skill: {skillId}");
        result.AppendLine();
        result.AppendLine($"Name: {skill.Name}");
        result.AppendLine($"Description: {skill.Description}");
        result.AppendLine();
        result.AppendLine("Content:");
        result.AppendLine("---");
        result.AppendLine(skill.SkillContent);
        result.AppendLine("---");
        return result.ToString();
    }

    private static string BuildResourceResponse(string skillId, string path, string resourceContent)
    {
        var result = new StringBuilder();
        result.AppendLine($"Successfully loaded resource from skill: {skillId}");
        result.AppendLine($"Resource path: {path}");
        result.AppendLine();
        result.AppendLine("Content:");
        result.AppendLine("---");
        result.AppendLine(resourceContent);
        result.AppendLine("---");
        return result.ToString();
    }

    private static string BuildResourceNotFoundMessage(
        string skillId,
        string path,
        IEnumerable<string> resourcePaths)
    {
        var message = new StringBuilder();
        message.AppendLine($"Resource not found: '{path}' in skill '{skillId}'.");
        message.AppendLine();
        message.AppendLine("Available resources:");

        var index = 1;
        message.AppendLine($"{index}. {SkillMarkdownPath}");
        foreach (var resourcePath in resourcePaths.OrderBy(static p => p, StringComparer.Ordinal))
        {
            index++;
            message.AppendLine($"{index}. {resourcePath}");
        }

        return message.ToString().TrimEnd();
    }
}
