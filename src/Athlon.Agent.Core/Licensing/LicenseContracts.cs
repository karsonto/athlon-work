namespace Athlon.Agent.Core.Licensing;

public interface ILicenseValidator
{
    LicenseValidationResult ValidateFile(string path);

    LicenseValidationResult ValidateContent(string content);
}

public interface ILicenseStore
{
    string UserConfigLicensePath { get; }

    void SaveToUserConfig(string content);
}

public interface IAdAccountResolver
{
    AdAccountInfo ResolveCurrent();
}
