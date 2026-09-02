using System.IO;

namespace Athlon.Agent.App.Services;

internal static class AthlonWebAssets
{
    public const string EntryFileName = "index.html";

    public static string AssetsDirectory
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "Assets", "AthlonWeb");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            // Dev: project Assets next to output
            var alt = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets", "AthlonWeb"));
            return Directory.Exists(alt) ? alt : candidate;
        }
    }
}
