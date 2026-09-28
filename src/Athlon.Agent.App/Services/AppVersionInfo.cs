using System.Reflection;

namespace Athlon.Agent.App.Services;

internal static class AppVersionInfo
{
    public const string ProductName = "Athlon Agent";

    public static string VersionDisplay
    {
        get
        {
            var assembly = typeof(AppVersionInfo).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational.Split('+')[0];
            }

            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
    }

    /// <summary>
    /// Assembly / file version (e.g. <c>3.0.1.0</c>). Reported to the management dashboard next to
    /// <see cref="VersionDisplay"/> because the two currently disagree: the csproj pins 3.0.1 while
    /// Release publishes with <c>-p:Version=&lt;tag&gt;</c>. Both are sent so a mismatch is visible
    /// rather than silently resolving to the wrong one.
    /// </summary>
    public static string FileVersion
    {
        get
        {
            try
            {
                var assembly = typeof(AppVersionInfo).Assembly;
                var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                if (!string.IsNullOrWhiteSpace(fileVersion))
                {
                    return fileVersion;
                }

                return assembly.GetName().Version?.ToString(4) ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
