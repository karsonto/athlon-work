using Athlon.Agent.App.Resources;
using Athlon.Agent.Core.Licensing;

namespace Athlon.Agent.App.Licensing;

internal static class LicenseFailureMessages
{
    public static string Describe(LicenseFailureCode code) => code switch
    {
        LicenseFailureCode.Missing => Strings.Get("License_Missing"),
        LicenseFailureCode.InvalidFormat => Strings.Get("License_InvalidFormat"),
        LicenseFailureCode.InvalidSignature => Strings.Get("License_InvalidSignature"),
        LicenseFailureCode.InvalidPayload => Strings.Get("License_InvalidPayload"),
        LicenseFailureCode.WrongProduct => Strings.Get("License_WrongProduct"),
        LicenseFailureCode.UnsupportedVersion => Strings.Get("License_UnsupportedVersion"),
        LicenseFailureCode.Expired => Strings.Get("License_Expired"),
        LicenseFailureCode.AccountMismatch => Strings.Get("License_AccountMismatch"),
        _ => Strings.Get("License_ValidationFailed"),
    };
}
