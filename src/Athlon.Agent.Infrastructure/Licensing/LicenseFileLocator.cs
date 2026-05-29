using Athlon.Agent.Core.Licensing;

namespace Athlon.Agent.Infrastructure.Licensing;

// AppPathProvider lives in parent namespace.
using AppPathProvider = Athlon.Agent.Infrastructure.AppPathProvider;

public sealed class LicenseFileLocator
{
    public const string LicenseFileName = "license.lic";

    public static string InstallDirectoryLicensePath =>
        Path.Combine(AppContext.BaseDirectory, LicenseFileName);

    public static string UserConfigLicensePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppPathProvider.AppDataFolderName,
            "config",
            LicenseFileName);

    public static IReadOnlyList<string> GetSearchPaths()
    {
        return [InstallDirectoryLicensePath, UserConfigLicensePath];
    }

    public static string? TryResolveExisting()
    {
        foreach (var path in GetSearchPaths())
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
