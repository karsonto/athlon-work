using System.Text.Json;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure.SkillHub;

internal static class SkillAnchorStore
{
    public static long Get(string anchorsPath, string skillId)
    {
        if (!File.Exists(anchorsPath))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(anchorsPath));
            if (document.RootElement.TryGetProperty(skillId, out var value) && value.TryGetInt64(out var seq))
            {
                return seq;
            }
        }
        catch (JsonException)
        {
            return 0;
        }

        return 0;
    }

    public static void Advance(string anchorsPath, string skillId, long commitSeq)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        if (File.Exists(anchorsPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(anchorsPath));
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.TryGetInt64(out var seq))
                    {
                        map[property.Name] = seq;
                    }
                }
            }
            catch (JsonException)
            {
                map.Clear();
            }
        }

        var current = map.TryGetValue(skillId, out var existing) ? existing : 0;
        if (commitSeq < current)
        {
            throw new InvalidOperationException("commitSeq is older than the anchored version.");
        }

        map[skillId] = commitSeq;
        var directory = Path.GetDirectoryName(anchorsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var ordered = map.OrderBy(static pair => pair.Key, StringComparer.Ordinal);
        var builder = new System.Text.StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var pair in ordered)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append('"').Append(Escape(pair.Key)).Append("\":").Append(pair.Value);
        }

        builder.Append('}');
        File.WriteAllText(anchorsPath, builder.ToString());
    }

    public static string ManifestPath(string manifestDirectory, string skillId) =>
        Path.Combine(manifestDirectory, SkillManifest.SafeFileName(skillId) + ".json");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
