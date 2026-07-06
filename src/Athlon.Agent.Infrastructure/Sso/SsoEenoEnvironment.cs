using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

internal static class SsoEenoEnvironment
{
    internal const string EnvVarName = "ATHLON_EENO";
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
            var pem = File.ReadAllText(publicKeyPath);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var cipher = rsa.Encrypt(Encoding.UTF8.GetBytes(userId), RSAEncryptionPadding.Pkcs1);
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
