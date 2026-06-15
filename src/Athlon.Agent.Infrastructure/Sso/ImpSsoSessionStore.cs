using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

[SupportedOSPlatform("windows")]
public sealed class ImpSsoSessionStore(IAppPathProvider paths) : IImpSsoSessionStore
{
    private const string SessionFileName = "sso-session.secret";
    private readonly object _lock = new();
    private ImpSsoSession? _cached;

    public ImpSsoSession? GetCachedSession()
    {
        lock (_lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var path = GetSessionPath();
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var encoded = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(encoded))
                {
                    return null;
                }

                var protectedBytes = Convert.FromBase64String(encoded);
                var plainBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    optionalEntropy: null,
                    DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plainBytes);
                _cached = JsonSerializer.Deserialize<ImpSsoSession>(json, ImpSsoJson.Options);
                return _cached;
            }
            catch
            {
                return null;
            }
        }
    }

    public void SaveSession(ImpSsoSession session)
    {
        lock (_lock)
        {
            _cached = session;
            Directory.CreateDirectory(paths.CredentialsPath);
            var json = JsonSerializer.Serialize(session, ImpSsoJson.Options);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(
                plainBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            File.WriteAllText(GetSessionPath(), Convert.ToBase64String(protectedBytes));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cached = null;
            var path = GetSessionPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public bool IsExpired(ImpSsoSession session) =>
        session.IsExpired(DateTimeOffset.UtcNow);

    private string GetSessionPath() => Path.Combine(paths.CredentialsPath, SessionFileName);
}

internal static class ImpSsoJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
