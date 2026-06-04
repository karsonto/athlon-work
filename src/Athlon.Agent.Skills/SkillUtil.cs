namespace Athlon.Agent.Skills;

public static class SkillUtil
{
    public const string SkillFileName = "SKILL.md";

    public static AgentSkill CreateFrom(
        string skillMd,
        IReadOnlyDictionary<string, string>? resources = null,
        IReadOnlyList<string>? resourcePaths = null,
        string? skillDirectory = null)
    {
        var parsed = MarkdownSkillParser.Parse(skillMd);
        var metadata = parsed.Metadata.ToDictionary(static pair => pair.Key, static pair => pair.Value);

        if (!metadata.TryGetValue("name", out var nameValue)
            || nameValue is not string name
            || string.IsNullOrWhiteSpace(name)
            || !metadata.TryGetValue("description", out var descriptionValue)
            || descriptionValue is not string description
            || string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException(
                "The SKILL.md must have a YAML Front Matter including `name` and `description` fields.");
        }

        if (string.IsNullOrWhiteSpace(parsed.Content))
        {
            throw new ArgumentException(
                "The SKILL.md must have content except for the YAML Front Matter.");
        }

        metadata["name"] = name;
        metadata["description"] = description;

        return new AgentSkill(
            metadata,
            parsed.Content,
            resources ?? new Dictionary<string, string>(),
            resourcePaths,
            skillDirectory);
    }
}
