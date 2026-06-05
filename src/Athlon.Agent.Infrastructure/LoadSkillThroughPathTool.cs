using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class LoadSkillThroughPathTool(ISkillRuntime skillRuntime) : IAgentTool
{
    public ToolDefinition Definition => BuildDefinition();

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!invocation.Arguments.TryGetValue("skillId", out var skillId) || string.IsNullOrWhiteSpace(skillId))
        {
            return Task.FromResult(ToolResult.Failure("Missing skillId", "Missing or empty required parameter: skillId"));
        }

        if (!invocation.Arguments.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(ToolResult.Failure("Missing path", "Missing or empty required parameter: path"));
        }

        try
        {
            var content = skillRuntime.LoadResource(skillId.Trim(), path.Trim());
            return Task.FromResult(ToolResult.Success($"Loaded skill resource from {skillId}", content));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Failure("Skill load failed", ex.Message));
        }
    }

    private ToolDefinition BuildDefinition()
    {
        var skills = skillRuntime.GetSkills();
        var skillIdList = skills.Count == 0
            ? "(none — install skills under the skills directory first)"
            : string.Join(", ", skills.Select(skill => skill.SkillId));

        return new ToolDefinition(
            "load_skill_through_path",
            "Load and activate a skill resource by name and resource path.\n\n"
            + "**Functionality:**\n"
            + "1. Activates the specified skill\n"
            + "2. Returns the requested resource content\n\n"
            + "**Path rules:**\n"
            + "- Use path=\"SKILL.md\" to load the skill's markdown documentation.\n"
            + "- Use exact resource paths such as \"references/guide.md\".\n"
            + "- Do not use '.', './', directories only, or absolute paths.\n"
            + "- The response includes Files root when the skill is on disk — use it for execute_command script paths.\n\n"
            + $"**Available skill names:** {skillIdList}",
            new Dictionary<string, string>
            {
                ["skillId"] = "The skill name from SKILL.md frontmatter (see Available skill names in the description).",
                ["path"] = "Relative resource path within the skill. Use 'SKILL.md' for full instructions."
            });
    }
}
