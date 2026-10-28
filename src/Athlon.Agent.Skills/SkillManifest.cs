using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Athlon.Agent.Skills;

/// <summary>
/// Canonical skill manifest shared with the platform and tools/sign_skill.py.
/// </summary>
public static class SkillManifest
{
    public sealed record FileEntry(string Path, long Size, string Sha256);

    public sealed record Parsed(string SkillId, long CommitSeq, IReadOnlyList<FileEntry> Files);

    public static string Canonical(string skillId, long commitSeq, IReadOnlyList<FileEntry> files)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("skillId is required.", nameof(skillId));
        }

        if (commitSeq < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(commitSeq));
        }

        var sorted = (files ?? []).OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray();
        var builder = new StringBuilder(256);
        builder.Append("{\"commitSeq\":").Append(commitSeq.ToString(CultureInfo.InvariantCulture)).Append(",\"files\":[");
        for (var i = 0; i < sorted.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var entry = sorted[i];
            ValidateEntry(entry);
            builder.Append("{\"path\":");
            AppendEscaped(builder, entry.Path);
            builder.Append(",\"sha256\":");
            AppendEscaped(builder, entry.Sha256);
            builder.Append(",\"size\":").Append(entry.Size.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        builder.Append("],\"skillId\":");
        AppendEscaped(builder, skillId.Trim());
        builder.Append('}');
        return builder.ToString();
    }

    public static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data ?? []);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool TryParse(string json, out Parsed? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("skillId", out var skillIdNode)
                || skillIdNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("commitSeq", out var seqNode)
                || !seqNode.TryGetInt64(out var commitSeq)
                || !root.TryGetProperty("files", out var filesNode)
                || filesNode.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var files = new List<FileEntry>();
            foreach (var file in filesNode.EnumerateArray())
            {
                if (!file.TryGetProperty("path", out var pathNode)
                    || !file.TryGetProperty("sha256", out var shaNode)
                    || !file.TryGetProperty("size", out var sizeNode)
                    || !sizeNode.TryGetInt64(out var size))
                {
                    return false;
                }

                files.Add(new FileEntry(pathNode.GetString() ?? "", size, shaNode.GetString() ?? ""));
            }

            manifest = new Parsed(skillIdNode.GetString() ?? "", commitSeq, files);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void ValidateEntry(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateRelativePath(entry.Path);
        if (entry.Size < 0 || entry.Sha256 is null || entry.Sha256.Length != 64)
        {
            throw new ArgumentException("manifest entry is invalid.");
        }

        foreach (var ch in entry.Sha256)
        {
            var hex = (ch is >= '0' and <= '9') || (ch is >= 'a' and <= 'f');
            if (!hex)
            {
                throw new ArgumentException("manifest sha256 is invalid.");
            }
        }
    }

    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '/' || path.Contains('\\') || path.Contains(':'))
        {
            throw new ArgumentException("illegal skill path.");
        }

        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or "..")
            {
                throw new ArgumentException("illegal skill path.");
            }
        }
    }

    public static string SafeFileName(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("skillId is required.", nameof(skillId));
        }

        var builder = new StringBuilder(skillId.Trim().Length);
        foreach (var ch in skillId.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_');
        }

        var name = builder.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            throw new ArgumentException("invalid skillId.");
        }

        return name;
    }

    private static void AppendEscaped(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
