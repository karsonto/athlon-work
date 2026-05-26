using System.Collections;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
namespace Athlon.Agent.Skills;

/// <summary>
/// Parses markdown files with YAML frontmatter (AgentScope-compatible).
/// </summary>
public static class MarkdownSkillParser
{
    private const int FrontmatterCodePointLimit = 16_384;

    private static readonly Regex FrontmatterPattern = new(
        @"^---\s*[\r\n]+(.*?)[\r\n]*---(?:\s*[\r\n]+)?(.*)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public static ParsedMarkdown Parse(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new ParsedMarkdown(new Dictionary<string, object>(), string.Empty);
        }

        var match = FrontmatterPattern.Match(markdown);
        if (!match.Success)
        {
            return new ParsedMarkdown(new Dictionary<string, object>(), markdown);
        }

        var yamlContent = match.Groups[1].Value.Trim();
        var markdownContent = match.Groups[2].Value;

        if (yamlContent.Length == 0)
        {
            return new ParsedMarkdown(new Dictionary<string, object>(), markdownContent);
        }

        return new ParsedMarkdown(ParseYamlMetadata(yamlContent), markdownContent);
    }

    public static string Generate(IReadOnlyDictionary<string, object> metadata, string content)
    {
        var serializer = new SerializerBuilder().Build();

        var builder = new System.Text.StringBuilder();
        if (metadata is { Count: > 0 })
        {
            builder.AppendLine("---");
            builder.Append(serializer.Serialize(metadata.ToDictionary(static pair => pair.Key, static pair => pair.Value)));
            builder.AppendLine("---");
        }

        if (!string.IsNullOrEmpty(content))
        {
            if (metadata is { Count: > 0 })
            {
                builder.AppendLine();
            }

            builder.Append(content);
        }

        return builder.ToString();
    }

    private static Dictionary<string, object> ParseYamlMetadata(string yamlContent)
    {
        if (yamlContent.Length > FrontmatterCodePointLimit)
        {
            return new Dictionary<string, object>();
        }

        object? loaded;
        try
        {
            loaded = YamlDeserializer.Deserialize<object>(yamlContent);
        }
        catch
        {
            return new Dictionary<string, object>();
        }

        if (loaded is not IDictionary<object, object> rawMap)
        {
            return new Dictionary<string, object>();
        }

        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in rawMap)
        {
            if (key is not string stringKey)
            {
                continue;
            }

            metadata[stringKey] = NormalizeMetadataValue(value)!;
        }

        return metadata;
    }

    private static object? NormalizeMetadataValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IDictionary<object, object> rawMap)
        {
            var normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (key, entryValue) in rawMap)
            {
                var normalizedKey = key is string stringKey ? stringKey : key.ToString() ?? string.Empty;
                normalized[normalizedKey] = NormalizeMetadataValue(entryValue)!;
            }

            return normalized;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeMetadataValue(item));
            }

            return list;
        }

        return value;
    }

    public sealed class ParsedMarkdown
    {
        public ParsedMarkdown(IReadOnlyDictionary<string, object> metadata, string content)
        {
            Metadata = metadata ?? new Dictionary<string, object>();
            Content = content ?? string.Empty;
        }

        public IReadOnlyDictionary<string, object> Metadata { get; }

        public string Content { get; }

        public bool HasFrontmatter => Metadata.Count > 0;
    }
}
