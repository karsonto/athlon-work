namespace Athlon.Agent.Skills;

/// <summary>
/// Agent skill loaded from SKILL.md (YAML frontmatter + markdown body) and optional resource files.
/// </summary>
public sealed class AgentSkill
{
    public AgentSkill(
        IReadOnlyDictionary<string, object> metadata,
        string skillContent,
        IReadOnlyDictionary<string, string> resources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillContent);

        var name = GetRequiredMetadataString(metadata, "name");
        var description = GetRequiredMetadataString(metadata, "description");

        var metadataCopy = new Dictionary<string, object>(metadata, StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = description
        };

        Metadata = metadataCopy;
        SkillContent = skillContent;
        Resources = resources is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(resources, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, object> Metadata { get; }

    public string Name => (string)Metadata["name"];

    public string Description => (string)Metadata["description"];

    public string SkillContent { get; }

    public IReadOnlyDictionary<string, string> Resources { get; }

    public string SkillId => Name;

    public string? GetResource(string resourcePath) =>
        Resources.TryGetValue(resourcePath, out var content) ? content : null;

    private static string GetRequiredMetadataString(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (metadata is null
            || !metadata.TryGetValue(key, out var value)
            || value is not string stringValue
            || string.IsNullOrWhiteSpace(stringValue))
        {
            throw new ArgumentException("The skill must have `name` and `description` fields.");
        }

        return stringValue;
    }
}
