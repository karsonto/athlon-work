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

    internal static void TryApply(ProcessStartInfo startInfo) =>
        TryApplyCore(value => startInfo.Environment[EnvVarName] = value);

    /// <summary>
    /// Inject <see cref="EnvVarName"/> into a process environment dictionary
    /// (e.g. MCP stdio <c>StdioClientTransport</c> environment).
    /// </summary>
    internal static void TryApply(IDictionary<string, string> environment) =>
        TryApplyCore(value => environment[EnvVarName] = value);

    private static void TryApplyCore(Action<string> setEnvironmentValue)
    {
        var encrypted = TestOverride?.Invoke() ?? TryGetStoredEncryptedValueDefault();
        if (!string.IsNullOrEmpty(encrypted))
        {
            setEnvironmentValue(encrypted);
        }
    }

    /// <summary>
    /// 登录成功后为 session 生成 jti 与 MCP refresh token，并随 session 持久化。
    /// </summary>
    internal static ImpSsoSession EnrichSessionWithMcpToken(ImpSsoSession session, IAppPathProvider paths)
    {
        if (!string.IsNullOrWhiteSpace(session.McpRefreshToken) && !string.IsNullOrWhiteSpace(session.Jti))
        {
            return session;
        }

        var userId = session.UserId?.Trim();
        if (string.IsNullOrEmpty(userId))
        {
            return session;
        }

        var publicKeyPath = Path.Combine(paths.ConfigPath, PublicKeyFileName);
        if (!File.Exists(publicKeyPath))
        {
            return session;
        }

        try
        {
            var jti = session.Jti ?? GenerateJti();
            var encrypted = EncryptSessionToken(session, jti, publicKeyPath);
            if (encrypted is null)
            {
                return session;
            }

            return new ImpSsoSession
            {
                SsoToken = session.SsoToken,
                UserId = session.UserId,
                DisplayName = session.DisplayName,
                LoggedInAt = session.LoggedInAt,
                ExpiresAt = session.ExpiresAt,
                Jti = jti,
                McpRefreshToken = encrypted
            };
        }
        catch
        {
            return session;
        }
    }

    internal static string? TryGetStoredEncryptedValue(
        AppSettings settings,
        IImpSsoSessionStore sessionStore)
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

        return string.IsNullOrWhiteSpace(session.McpRefreshToken) ? null : session.McpRefreshToken;
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

    private static string? EncryptSessionToken(ImpSsoSession session, string jti, string publicKeyPath)
    {
        var tokenData = new
        {
            user_id = session.UserId.Trim(),
            token_type = "refresh",
            jti,
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

    private static string? TryGetStoredEncryptedValueDefault()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var settings = AppSettingsLoader.Load();
            var store = new ImpSsoSessionStore(new AppPathProvider());
            return TryGetStoredEncryptedValue(settings, store);
        }
        catch
        {
            return null;
        }
    }
}
