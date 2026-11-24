using System.Formats.Tar;
using System.IO;
using System.Text;

namespace Athlon.Agent.App.Services.Room;

internal static class RoomDockerTar
{
    public static MemoryStream CreateSingleFileTar(string fileName, byte[] content)
    {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName)
            {
                DataStream = new MemoryStream(content),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            };
            writer.WriteEntry(entry);
        }

        ms.Position = 0;
        return ms;
    }

    public static byte[]? ExtractFirstFileFromTar(byte[] tarBytes)
    {
        using var ms = new MemoryStream(tarBytes);
        using var reader = new TarReader(ms);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                && entry.DataStream is not null)
            {
                using var copy = new MemoryStream();
                entry.DataStream.CopyTo(copy);
                return copy.ToArray();
            }
        }

        return null;
    }

    public static MemoryStream CreateDockerfileContextTar(string dockerfileContent, string dockerfileName = "Dockerfile")
    {
        return CreateBuildContextTar(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [dockerfileName] = Encoding.UTF8.GetBytes(dockerfileContent)
        });
    }

    public static MemoryStream CreateBuildContextTar(IReadOnlyDictionary<string, byte[]> files)
    {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name.Replace('\\', '/'))
                {
                    DataStream = new MemoryStream(content),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                        | UnixFileMode.OtherRead | UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherExecute
                };
                writer.WriteEntry(entry);
            }
        }

        ms.Position = 0;
        return ms;
    }
}
