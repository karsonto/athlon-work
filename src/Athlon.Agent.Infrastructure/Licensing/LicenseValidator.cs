using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Licensing;

namespace Athlon.Agent.Infrastructure.Licensing;

public sealed class LicenseValidator : ILicenseValidator
{
    public const string ExpectedProduct = "athlon-agent";
    public const int SupportedVersion = 1;

    private readonly IAdAccountResolver _accountResolver;
    private readonly RSA _publicKey;

    public LicenseValidator(IAdAccountResolver accountResolver)
        : this(accountResolver, ImportProductionPublicKey())
    {
    }

    public LicenseValidator(IAdAccountResolver accountResolver, RSA publicKey)
    {
        _accountResolver = accountResolver;
        _publicKey = publicKey;
    }

    public LicenseValidationResult ValidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.Missing,
                "未找到 License 文件。");
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.InvalidFormat,
                $"无法读取 License 文件：{ex.Message}");
        }

        return ValidateContent(content);
    }

    public LicenseValidationResult ValidateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.Missing,
                "License 内容为空。");
        }

        SignedLicenseEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedLicenseEnvelope>(
                content,
                LicenseJson.Options);
        }
        catch (JsonException ex)
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.InvalidFormat,
                $"License 格式无效：{ex.Message}");
        }

        if (envelope is null
            || string.IsNullOrWhiteSpace(envelope.PayloadB64)
            || string.IsNullOrWhiteSpace(envelope.SignatureB64))
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.InvalidFormat,
                "License 缺少 payloadB64 或 signatureB64。");
        }

        if (!VerifySignature(envelope))
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.InvalidSignature,
                "License 签名无效。");
        }

        LicensePayload? payload;
        try
        {
            var payloadBytes = Convert.FromBase64String(envelope.PayloadB64);
            payload = JsonSerializer.Deserialize<LicensePayload>(
                Encoding.UTF8.GetString(payloadBytes),
                LicenseJson.Options);
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.InvalidPayload,
                $"License 载荷无效：{ex.Message}");
        }

        if (payload is null)
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.InvalidPayload,
                "License 载荷为空。");
        }

        if (payload.Version != SupportedVersion)
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.UnsupportedVersion,
                $"不支持的 License 版本：{payload.Version}。");
        }

        if (!string.Equals(payload.Product, ExpectedProduct, StringComparison.Ordinal))
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.WrongProduct,
                $"License 产品不匹配：{payload.Product}。");
        }

        if (DateTimeOffset.UtcNow > payload.ExpiresAt)
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.Expired,
                $"License 已于 {payload.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC 过期。");
        }

        var account = _accountResolver.ResolveCurrent();
        if (!AccountMatches(payload, account))
        {
            return LicenseValidationResult.Fail(
                LicenseFailureCode.AccountMismatch,
                BuildAccountMismatchMessage(payload, account));
        }

        return LicenseValidationResult.Success(payload);
    }

    public static bool AccountMatches(LicensePayload payload, AdAccountInfo account)
    {
        var currentSam = Normalize(account.SamAccountName);
        var currentUpn = Normalize(account.UserPrincipalName);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(allowed, payload.AdAccount);
        AddIfPresent(allowed, payload.AdAccountSam);
        AddIfPresent(allowed, payload.AdAccountUpn);

        if (allowed.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(currentSam) && allowed.Contains(currentSam))
        {
            return true;
        }

        return !string.IsNullOrEmpty(currentUpn) && allowed.Contains(currentUpn);
    }

    private bool VerifySignature(SignedLicenseEnvelope envelope)
    {
        try
        {
            var payloadBytes = Encoding.UTF8.GetBytes(envelope.PayloadB64);
            var signatureBytes = Convert.FromBase64String(envelope.SignatureB64);
            return _publicKey.VerifyData(
                payloadBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static void AddIfPresent(HashSet<string> set, string? value)
    {
        var normalized = Normalize(value);
        if (!string.IsNullOrEmpty(normalized))
        {
            set.Add(normalized);
        }
    }

    private static string BuildAccountMismatchMessage(LicensePayload payload, AdAccountInfo account)
    {
        var licensed = new List<string>();
        AddIfPresentToList(licensed, payload.AdAccount);
        AddIfPresentToList(licensed, payload.AdAccountSam);
        AddIfPresentToList(licensed, payload.AdAccountUpn);

        var licensedText = licensed.Count > 0
            ? string.Join(" / ", licensed.Distinct(StringComparer.OrdinalIgnoreCase))
            : "(未知)";

        var upnText = string.IsNullOrWhiteSpace(account.UserPrincipalName)
            ? "(无)"
            : account.UserPrincipalName;

        return $"License 绑定的账号为 {licensedText}，当前登录为 {account.SamAccountName}（UPN: {upnText}）。";
    }

    private static void AddIfPresentToList(List<string> list, string? value)
    {
        var normalized = Normalize(value);
        if (!string.IsNullOrEmpty(normalized))
        {
            list.Add(normalized);
        }
    }

    private static RSA ImportProductionPublicKey()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(LicensePublicKey.Pem);
        return rsa;
    }
}

internal static class LicenseJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
