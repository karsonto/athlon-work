using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class LoadSkillThroughPathTool(ISkillRuntime skillRuntime) : IAgentTool
{
    private static readonly ToolDefinition StaticDefinition = new(
        "load_skill_through_path",
        "Load and activate a skill resource by skill name and resource path. "
        + "Use path=\"SKILL.md\" for instructions or an exact skill-internal relative resource path. "
        + "Do not use '.', './', directories, or absolute paths. Available skills are listed once in the skill catalog prompt.",
        ToolSchema.Object()
            .String("skillId", "Skill name from the available skill catalog.", required: true, minLength: 1)
            .String("path", "Relative resource path within the skill. Use 'SKILL.md' for full instructions.", required: true, minLength: 1)
            .Build());

    public ToolDefinition Definition => StaticDefinition;

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!invocation.Arguments.TryGetString("skillId", out var skillId) || string.IsNullOrWhiteSpace(skillId))
        {
            return Task.FromResult(ToolResult.Failure("Missing skillId", "Missing or empty required parameter: skillId"));
        }

        if (!invocation.Arguments.TryGetString("path", out var path) || string.IsNullOrWhiteSpace(path))
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
}
