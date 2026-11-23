using System.IO;

namespace Athlon.Agent.App.Services.Room;

internal static class AthlonRoomAssets
{
    public const string EntryFileName = "index.html";

    public static string AssetsDirectory
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "Assets", "AthlonRoom");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, EntryFileName)))
            {
                return candidate;
            }

            var alt = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets", "AthlonRoom"));
            return Directory.Exists(alt) ? alt : candidate;
        }
    }
}
