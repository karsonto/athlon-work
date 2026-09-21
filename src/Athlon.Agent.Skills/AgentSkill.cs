namespace Athlon.Agent.Skills;

/// <summary>
/// Agent skill loaded from SKILL.md (YAML frontmatter + markdown body) and optional resource files.
/// </summary>
public sealed class AgentSkill
{
    public AgentSkill(
        IReadOnlyDictionary<string, object> metadata,
        string skillContent,
        IReadOnlyDictionary<string, string>? resources = null,
        IReadOnlyList<string>? resourcePaths = null,
        string? skillDirectory = null,
        Func<IReadOnlyList<string>>? listResourcePaths = null)
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
        // Only a caller-supplied list short-circuits the lazy path; otherwise the filesystem walk
        // is deferred until ResourcePaths is actually read. The lambda captures skillDir rather
        // than this, so it stays valid during construction.
        _resourcePaths = resourcePaths;
        _listResourcePaths = listResourcePaths ?? (() => Array.Empty<string>());
        SkillDirectory = skillDirectory;
    }

    /// <summary>Backing field for the lazily resolved <see cref="ResourcePaths"/>.</summary>
    private IReadOnlyList<string>? _resourcePaths;

    private readonly Func<IReadOnlyList<string>> _listResourcePaths;

    public IReadOnlyDictionary<string, object> Metadata { get; }

    public string Name => (string)Metadata["name"];

    public string Description => (string)Metadata["description"];

    public string SkillContent { get; }

    /// <summary>In-memory resource payloads (optional; catalog loads metadata-only by default).</summary>
    public IReadOnlyDictionary<string, string> Resources { get; }

    public string SkillId => Name;

    /// <summary>
    /// Relative resource paths under <see cref="SkillDirectory"/>, resolved on first access.
    ///
    /// <para>Lazy because listing resources recursively enumerates every file under the skill
    /// folder, and the only consumer is the "resource not found" error message in
    /// <c>SkillRuntime</c>. Doing it eagerly made each catalog load pay a full directory walk per
    /// skill — measured at ~10s on a session switch, for a string that is almost never read.</para>
    /// </summary>
    public IReadOnlyList<string> ResourcePaths
    {
        get
        {
            var cached = _resourcePaths;
            if (cached is not null)
            {
                return cached;
            }

            // Benign race: two threads may both walk the folder and publish an equivalent list.
            // Deliberately not locked — this is a property read on a path that is almost never hit.
            var resolved = _listResourcePaths();
            _resourcePaths = resolved;
            return resolved;
        }
    }

    /// <summary>Skill folder on disk; used to load resources on demand.</summary>
    public string? SkillDirectory { get; }

    public bool SupportsLazyResourceLoad =>
        !string.IsNullOrWhiteSpace(SkillDirectory) && Directory.Exists(SkillDirectory);

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
