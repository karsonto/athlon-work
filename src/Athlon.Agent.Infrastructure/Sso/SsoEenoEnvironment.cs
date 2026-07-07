using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

/// <summary>
/// MCP Refresh Token 加密与环境变量注入
/// 使用 RSA-OAEP (SHA256) 加密包含用户信息的 JSON Token
/// </summary>
internal static class SsoEenoEnvironment
{
    internal const string EnvVarName = "MCP_REFRESH_TOKEN";
    internal const string PublicKeyFileName = "public_key.pem";

    internal static Func<string?>? TestOverride { get; set; }

    internal static void TryApply(ProcessStartInfo startInfo)
    {
        var encrypted = TestOverride?.Invoke() ?? TryGetEncryptedValueDefault();
        if (!string.IsNullOrEmpty(encrypted))
        {
            startInfo.Environment[EnvVarName] = encrypted;
        }
    }

    private static string FormatUtcSeconds(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>
    /// 生成 URL-safe 的 Token 唯一标识（模仿 Python secrets.token_urlsafe(16)）
    /// </summary>
    private static string GenerateJti()
    {
        byte[] randomBytes = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);

        return Convert.ToBase64String(randomBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static string? TryGetEncryptedValue(
        AppSettings settings,
        IImpSsoSessionStore sessionStore,
        IAppPathProvider paths)
    {
        if (!settings.Sso.Enabled)
        {
            return null;
        }

        var session = sessionStore.GetCachedSession();
        if (session is null || sessionStore.IsExpired(session))
        {
            return null;
        }

        var userId = session.UserId?.Trim();
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        var publicKeyPath = Path.Combine(paths.ConfigPath, PublicKeyFileName);
        if (!File.Exists(publicKeyPath))
        {
            return null;
        }

        try
        {
            var tokenData = new
            {
                user_id = userId,
                token_type = "refresh",
                jti = GenerateJti(),
                expires_at = FormatUtcSeconds(session.ExpiresAt),
                issued_at = FormatUtcSeconds(session.LoggedInAt)
            };

            string json = JsonSerializer.Serialize(tokenData);

            var pem = File.ReadAllText(publicKeyPath);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);

            var cipher = rsa.Encrypt(
                Encoding.UTF8.GetBytes(json),
                RSAEncryptionPadding.OaepSHA256
            );

            return Convert.ToBase64String(cipher);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetEncryptedValueDefault()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var paths = new AppPathProvider();
            var settings = AppSettingsLoader.Load();
            var store = new ImpSsoSessionStore(paths);
            return TryGetEncryptedValue(settings, store, paths);
        }
        catch
        {
            return null;
        }
    }
}
