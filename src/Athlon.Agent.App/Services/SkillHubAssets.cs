using System.IO;

namespace Athlon.Agent.App.Services;

internal static class SkillHubAssets
{
    public const string VirtualHost = "athlon-skill-hub.local";

    public static string AssetsDirectory
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "Assets", "SkillHub");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            // Dev: project Assets next to output
            var alt = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets", "SkillHub"));
            return Directory.Exists(alt) ? alt : candidate;
        }
    }

    public static string EntryUrl => $"https://{VirtualHost}/skill-hub.html";
}
