using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services.Room;

/// <summary>
/// One file in a push-oriented cloud sync manifest.
/// Comparison on the control plane uses <see cref="Sha256"/> only; <see cref="MtimeUnixMs"/> is metadata.
/// </summary>
internal sealed record CloudFileManifestEntry(string Path, long Size, long MtimeUnixMs, string Sha256);

/// <summary>
/// Pack / unpack host workspace and skills for cloud sandbox sync (desktop → control plane).
/// Incremental flow: build desired manifest → POST /sync/manifest → upload blobs listed in need/needUpload → POST /sync/delta.
/// </summary>
internal static class CloudWorkspaceSync
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        ".git",
        ".svn",
        ".hg",
        "__pycache__",
        ".venv",
        "venv",
        ".tox",
        "bin",
        "obj",
        ".vs",
        ".idea",
        "dist",
        "build",
        ".next",
        "target"
    };

    public static MemoryStream PackDirectoryTarGz(
        string hostRoot,
        IReadOnlyList<string>? onlyTopLevelNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostRoot);
        if (!Directory.Exists(hostRoot))
        {
            Directory.CreateDirectory(hostRoot);
        }

        var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            var root = Path.GetFullPath(hostRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            IEnumerable<string> topEntries = Directory.EnumerateFileSystemEntries(root);
            // null => include all; empty list => include nothing (selected-skills sync).
            if (onlyTopLevelNames is not null)
            {
                var allow = new HashSet<string>(onlyTopLevelNames, StringComparer.OrdinalIgnoreCase);
                topEntries = topEntries.Where(p => allow.Contains(Path.GetFileName(p)));
            }

            foreach (var entryPath in topEntries)
            {
                WriteEntryRecursive(writer, root, entryPath);
            }
        }

        // TarWriter Dispose writes two 512-byte EOF records; gzip footer must be present.
        if (ms.Length == 0)
        {
            throw new InvalidOperationException("PackDirectoryTarGz produced an empty archive.");
        }

        ms.Position = 0;
        return ms;
    }

    public static void UnpackTarGzToDirectory(Stream tarGz, string hostRoot)
    {
        ArgumentNullException.ThrowIfNull(tarGz);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostRoot);
        Directory.CreateDirectory(hostRoot);
        var root = Path.GetFullPath(hostRoot);

        using var gzip = new GZipStream(tarGz, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip);
        while (true)
        {
            TarEntry? entry;
            try
            {
                entry = reader.GetNextEntry();
            }
            catch (EndOfStreamException)
            {
                // Empty allow-list packs can yield a gzip that decompresses to 0 bytes;
                // TarReader then throws instead of returning null. Treat as end of archive.
                break;
            }

            if (entry is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = entry.Name.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var dest = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var outFs = File.Create(dest);
            if (entry.DataStream is not null)
            {
                entry.DataStream.CopyTo(outFs);
            }
        }
    }

    private static void WriteEntryRecursive(TarWriter writer, string root, string path)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        if (Directory.Exists(full))
        {
            if (IgnoredDirectoryNames.Contains(name))
            {
                return;
            }

            var relativeDir = ToTarRelative(root, full);
            if (!string.IsNullOrEmpty(relativeDir))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, relativeDir.TrimEnd('/') + "/")
                {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                });
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(full))
            {
                WriteEntryRecursive(writer, root, child);
            }

            return;
        }

        if (!File.Exists(full))
        {
            return;
        }

        var relative = ToTarRelative(root, full);
        if (string.IsNullOrEmpty(relative))
        {
            return;
        }

        using var fs = File.OpenRead(full);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, relative)
        {
            DataStream = fs,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
        };
        writer.WriteEntry(entry);
    }

    private static string ToTarRelative(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return "";
        }

        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Map room-agent selected skill <c>name</c>s (frontmatter) to on-disk top-level folder names.
    /// Empty / null selection ⇒ empty list (sync no skills). Never means “all skills”.
    /// </summary>
    public static IReadOnlyList<string> ResolveSelectedSkillFolders(
        string skillsRoot,
        IReadOnlyList<string>? selectedSkillNames)
    {
        if (selectedSkillNames is null || selectedSkillNames.Count == 0)
        {
            return Array.Empty<string>();
        }

        var nameToFolder = SkillFileSystemHelper.GetSkillNameToFolderMap(skillsRoot);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in selectedSkillNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = raw.Trim();
            if (nameToFolder.TryGetValue(name, out var folder) && !string.IsNullOrWhiteSpace(folder))
            {
                folders.Add(folder);
                continue;
            }

            var direct = Path.Combine(skillsRoot, name);
            if (SkillFileSystemHelper.HasSkillFile(direct))
            {
                folders.Add(name);
            }
        }

        return folders.ToArray();
    }

    /// <summary>
    /// Build the client's <c>desired</c> manifest for push sync.
    /// <paramref name="onlyTopLevelNames"/>: null = all entries; non-null (incl. empty) = only those top-level trees.
    /// </summary>
    public static List<CloudFileManifestEntry> BuildManifest(
        string hostRoot,
        IReadOnlyList<string>? onlyTopLevelNames = null)
    {
        var list = new List<CloudFileManifestEntry>();
        if (!Directory.Exists(hostRoot))
        {
            return list;
        }

        var root = Path.GetFullPath(hostRoot);
        IEnumerable<string> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        if (onlyTopLevelNames is not null)
        {
            var allow = new HashSet<string>(onlyTopLevelNames, StringComparer.OrdinalIgnoreCase);
            files = files.Where(f =>
            {
                var rel = Path.GetRelativePath(root, f);
                var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                return allow.Contains(top);
            });
        }

        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (relative.Split('/').Any(p => IgnoredDirectoryNames.Contains(p)))
            {
                continue;
            }

            var info = new FileInfo(full);
            list.Add(new CloudFileManifestEntry(
                relative,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                Sha256File(full)));
        }

        return list;
    }

    public static string SerializeManifest(IReadOnlyList<CloudFileManifestEntry> entries) =>
        JsonSerializer.Serialize(entries, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        var hash = SHA256.HashData(fs);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}
