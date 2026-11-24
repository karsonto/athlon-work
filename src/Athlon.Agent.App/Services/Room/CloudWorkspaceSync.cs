using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Pack / unpack host workspace and skills for cloud sandbox sync.</summary>
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
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            var root = Path.GetFullPath(hostRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            IEnumerable<string> topEntries = Directory.EnumerateFileSystemEntries(root);
            if (onlyTopLevelNames is { Count: > 0 })
            {
                var allow = new HashSet<string>(onlyTopLevelNames, StringComparer.OrdinalIgnoreCase);
                topEntries = topEntries.Where(p => allow.Contains(Path.GetFileName(p)));
            }

            foreach (var entryPath in topEntries)
            {
                WriteEntryRecursive(writer, root, entryPath);
            }
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
        while (reader.GetNextEntry() is { } entry)
        {
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
}
