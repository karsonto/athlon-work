using HandlebarsDotNet;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Athlon.Agent.Skills;

public sealed class SkillDefinition
{
    public string Name { get; set; } = "unnamed";
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public List<string> Triggers { get; set; } = new();
    public SkillEnvironment Env { get; set; } = new();
    public string Script { get; set; } = string.Empty;
}

public sealed class SkillEnvironment
{
    public string Runtime { get; set; } = "prompt";
    public List<string> Packages { get; set; } = new();
}

public sealed class SkillService
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public IReadOnlyList<SkillDefinition> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<SkillDefinition>();
        }

        return Directory.EnumerateFiles(directory, "*.y*ml")
            .Select(File.ReadAllText)
            .Select(content => _deserializer.Deserialize<SkillDefinition>(content))
            .Where(skill => skill is not null)
            .ToArray();
    }

    public string Render(SkillDefinition skill, object model)
    {
        var template = Handlebars.Compile(skill.Script);
        return template(model);
    }
}
