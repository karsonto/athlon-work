using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Licensing;
using Athlon.Agent.Infrastructure.Licensing;

namespace Athlon.Agent.Tests;

public sealed class LicenseValidatorTests
{
    private static readonly FakeAdAccountResolver DefaultAccount = new("CONTOSO\\jdoe", "jdoe@contoso.com");

    [Fact]
    public void ValidateContent_accepts_valid_sam_license()
    {
        using var keys = TestLicenseFactory.CreateKeyPair();
        var content = TestLicenseFactory.CreateSignedLicense(
            keys,
            DefaultAccount,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));

        var validator = new LicenseValidator(DefaultAccount, keys.PublicKey);
        var result = validator.ValidateContent(content);

        Assert.True(result.IsValid);
        Assert.Equal(LicenseFailureCode.None, result.FailureCode);
    }

    [Fact]
    public void ValidateContent_accepts_upn_when_license_has_upn()
    {
        using var keys = TestLicenseFactory.CreateKeyPair();
        var account = new FakeAdAccountResolver("CONTOSO\\jdoe", "jdoe@contoso.com");
        var content = TestLicenseFactory.CreateSignedLicense(
            keys,
            account,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            adAccount: "jdoe@contoso.com",
            adAccountSam: "CONTOSO\\jdoe",
            adAccountUpn: "jdoe@contoso.com");

        var validator = new LicenseValidator(account, keys.PublicKey);
        var result = validator.ValidateContent(content);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateContent_rejects_invalid_signature()
    {
        using var keys = TestLicenseFactory.CreateKeyPair();
        var content = TestLicenseFactory.CreateSignedLicense(
            keys,
            DefaultAccount,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));
        content = content.Replace('A', 'B', StringComparison.Ordinal);

        var validator = new LicenseValidator(DefaultAccount, keys.PublicKey);
        var result = validator.ValidateContent(content);

        Assert.False(result.IsValid);
        Assert.Equal(LicenseFailureCode.InvalidSignature, result.FailureCode);
    }

    [Fact]
    public void ValidateContent_rejects_expired_license()
    {
        using var keys = TestLicenseFactory.CreateKeyPair();
        var content = TestLicenseFactory.CreateSignedLicense(
            keys,
            DefaultAccount,
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var validator = new LicenseValidator(DefaultAccount, keys.PublicKey);
        var result = validator.ValidateContent(content);

        Assert.False(result.IsValid);
        Assert.Equal(LicenseFailureCode.Expired, result.FailureCode);
    }

    [Fact]
    public void ValidateContent_rejects_account_mismatch()
    {
        using var keys = TestLicenseFactory.CreateKeyPair();
        var content = TestLicenseFactory.CreateSignedLicense(
            keys,
            DefaultAccount,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            adAccount: "CONTOSO\\other");

        var validator = new LicenseValidator(DefaultAccount, keys.PublicKey);
        var result = validator.ValidateContent(content);

        Assert.False(result.IsValid);
        Assert.Equal(LicenseFailureCode.AccountMismatch, result.FailureCode);
    }

    [Fact]
    public void ValidateContent_rejects_wrong_product()
    {
        using var keys = TestLicenseFactory.CreateKeyPair();
        var content = TestLicenseFactory.CreateSignedLicense(
            keys,
            DefaultAccount,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            product: "other-product");

        var validator = new LicenseValidator(DefaultAccount, keys.PublicKey);
        var result = validator.ValidateContent(content);

        Assert.False(result.IsValid);
        Assert.Equal(LicenseFailureCode.WrongProduct, result.FailureCode);
    }

    [Fact]
    public void ValidateFile_reads_existing_file()
    {
        using var keys = TestLicenseFactory.CreateKeyPair();
        var content = TestLicenseFactory.CreateSignedLicense(
            keys,
            DefaultAccount,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));

        var path = Path.Combine(Path.GetTempPath(), $"athlon-lic-{Guid.NewGuid():N}.lic");
        try
        {
            File.WriteAllText(path, content);
            var validator = new LicenseValidator(DefaultAccount, keys.PublicKey);
            var result = validator.ValidateFile(path);
            Assert.True(result.IsValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AccountMatches_compares_sam_and_upn_aliases()
    {
        var payload = new LicensePayload(
            Version: 1,
            Product: LicenseValidator.ExpectedProduct,
            LicenseId: Guid.NewGuid().ToString(),
            IssuedAt: DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(30),
            AdAccount: "jdoe@contoso.com",
            AdAccountSam: "CONTOSO\\jdoe",
            AdAccountUpn: "jdoe@contoso.com");

        Assert.True(LicenseValidator.AccountMatches(
            payload,
            new AdAccountInfo("CONTOSO\\jdoe", null)));

        Assert.True(LicenseValidator.AccountMatches(
            payload,
            new AdAccountInfo("OTHER\\jdoe", "jdoe@contoso.com")));
    }

    private sealed class FakeAdAccountResolver(string sam, string? upn) : IAdAccountResolver
    {
        public AdAccountInfo ResolveCurrent() => new(sam, upn);
    }
}

internal static class TestLicenseFactory
{
    internal sealed class TestKeyPair : IDisposable
    {
        public RSA PrivateKey { get; }
        public RSA PublicKey { get; }

        public TestKeyPair(RSA privateKey, RSA publicKey)
        {
            PrivateKey = privateKey;
            PublicKey = publicKey;
        }

        public void Dispose()
        {
            PrivateKey.Dispose();
            PublicKey.Dispose();
        }
    }

    public static TestKeyPair CreateKeyPair()
    {
        var privateKey = RSA.Create(2048);
        var publicKey = RSA.Create();
        publicKey.ImportRSAPublicKey(privateKey.ExportRSAPublicKey(), out _);
        return new TestKeyPair(privateKey, publicKey);
    }

    public static string CreateSignedLicense(
        TestKeyPair keys,
        IAdAccountResolver account,
        DateTimeOffset expiresAt,
        string? adAccount = null,
        string? adAccountSam = null,
        string? adAccountUpn = null,
        string product = LicenseValidator.ExpectedProduct)
    {
        var current = account.ResolveCurrent();
        var payload = new
        {
            version = 1,
            product,
            licenseId = Guid.NewGuid().ToString(),
            issuedAt = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
            expiresAt = expiresAt.ToString("O"),
            adAccount = adAccount ?? current.SamAccountName,
            adAccountSam = adAccountSam ?? current.SamAccountName,
            adAccountUpn = adAccountUpn ?? current.UserPrincipalName,
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var signature = keys.PrivateKey.SignData(
            Encoding.UTF8.GetBytes(payloadB64),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var envelope = new
        {
            payloadB64,
            signatureB64 = Convert.ToBase64String(signature),
        };

        return JsonSerializer.Serialize(envelope);
    }
}
