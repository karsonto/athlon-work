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
}
