using Athlon.Agent.Core.Licensing;

namespace Athlon.Agent.Infrastructure.Licensing;

public sealed class LicenseStore : ILicenseStore
{
    public string UserConfigLicensePath => LicenseFileLocator.UserConfigLicensePath;

    public void SaveToUserConfig(string content)
    {
        var directory = Path.GetDirectoryName(UserConfigLicensePath)
            ?? throw new InvalidOperationException("Invalid license path.");
        Directory.CreateDirectory(directory);

        var tempPath = UserConfigLicensePath + ".tmp";
        File.WriteAllText(tempPath, content.Trim());
        File.Move(tempPath, UserConfigLicensePath, overwrite: true);
    }
}
