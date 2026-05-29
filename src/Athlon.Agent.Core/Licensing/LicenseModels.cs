namespace Athlon.Agent.Core.Licensing;

public sealed record LicensePayload(
    int Version,
    string Product,
    string LicenseId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string AdAccount,
    string? AdAccountSam,
    string? AdAccountUpn);

public sealed record SignedLicenseEnvelope(
    string PayloadB64,
    string SignatureB64);

public enum LicenseFailureCode
{
    None,
    Missing,
    InvalidFormat,
    InvalidSignature,
    InvalidPayload,
    WrongProduct,
    UnsupportedVersion,
    Expired,
    AccountMismatch
}

public sealed record LicenseValidationResult(
    bool IsValid,
    LicenseFailureCode FailureCode,
    string Message,
    LicensePayload? Payload = null)
{
    public static LicenseValidationResult Success(LicensePayload payload) =>
        new(true, LicenseFailureCode.None, string.Empty, payload);

    public static LicenseValidationResult Fail(LicenseFailureCode code, string message) =>
        new(false, code, message);
}

public sealed record AdAccountInfo(string SamAccountName, string? UserPrincipalName);
