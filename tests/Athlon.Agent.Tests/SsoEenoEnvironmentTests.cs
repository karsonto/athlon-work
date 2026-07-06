using System.Security.Cryptography;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Sso;

namespace Athlon.Agent.Tests;

public sealed class SsoEenoEnvironmentTests
{
    [Fact]
    public void TryGetEncryptedValue_ReturnsNull_WhenSsoDisabled()
    {
        var paths = CreatePaths();
        var store = new FakeSsoSessionStore(CreateValidSession());
        var settings = new AppSettings { Sso = { Enabled = false } };

        var encrypted = SsoEenoEnvironment.TryGetEncryptedValue(settings, store, paths);

        Assert.Null(encrypted);
    }

    [Fact]
    public void TryGetEncryptedValue_ReturnsNull_WhenSessionExpired()
    {
        var paths = CreatePaths();
        var store = new FakeSsoSessionStore(CreateExpiredSession());
        var settings = new AppSettings { Sso = { Enabled = true } };

        var encrypted = SsoEenoEnvironment.TryGetEncryptedValue(settings, store, paths);

        Assert.Null(encrypted);
    }

    [Fact]
    public void TryGetEncryptedValue_ReturnsNull_WhenPublicKeyMissing()
    {
        var paths = CreatePaths();
        var store = new FakeSsoSessionStore(CreateValidSession());
        var settings = new AppSettings { Sso = { Enabled = true } };

        var encrypted = SsoEenoEnvironment.TryGetEncryptedValue(settings, store, paths);

        Assert.Null(encrypted);
    }

    [Fact]
    public void TryGetEncryptedValue_ReturnsPkcs1Base64_WhenSsoValidAndPublicKeyPresent()
    {
        using var keys = CreateKeyPair();
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigPath);
        File.WriteAllText(
            Path.Combine(paths.ConfigPath, SsoEenoEnvironment.PublicKeyFileName),
            keys.PublicPem);

        var store = new FakeSsoSessionStore(CreateValidSession("000974115"));
        var settings = new AppSettings { Sso = { Enabled = true } };

        var encrypted = SsoEenoEnvironment.TryGetEncryptedValue(settings, store, paths);

        Assert.False(string.IsNullOrWhiteSpace(encrypted));
        var cipher = Convert.FromBase64String(encrypted!);
        var plain = keys.PrivateKey.Decrypt(cipher, RSAEncryptionPadding.Pkcs1);
        Assert.Equal("000974115", Encoding.UTF8.GetString(plain));
    }

    private static ImpSsoSession CreateValidSession(string userId = "000974115")
    {
        var now = DateTimeOffset.UtcNow;
        return new ImpSsoSession
        {
            SsoToken = "token",
            UserId = userId,
            DisplayName = "Test User",
            LoggedInAt = now,
            ExpiresAt = now.AddHours(24)
        };
    }

    private static ImpSsoSession CreateExpiredSession()
    {
        var now = DateTimeOffset.UtcNow;
        return new ImpSsoSession
        {
            SsoToken = "token",
            UserId = "000974115",
            DisplayName = "Test User",
            LoggedInAt = now.AddHours(-25),
            ExpiresAt = now.AddHours(-1)
        };
    }

    private static TestAppPathProvider CreatePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-sso-eeno-" + Guid.NewGuid().ToString("N"));
        return new TestAppPathProvider(root);
    }

    private static TestKeyPair CreateKeyPair()
    {
        var privateKey = RSA.Create(2048);
        var publicPem = privateKey.ExportSubjectPublicKeyInfoPem();
        return new TestKeyPair(privateKey, publicPem);
    }

    private sealed class FakeSsoSessionStore(ImpSsoSession? session) : IImpSsoSessionStore
    {
        public ImpSsoSession? GetCachedSession() => session;

        public void SaveSession(ImpSsoSession session) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool IsExpired(ImpSsoSession session) => session.IsExpired(DateTimeOffset.UtcNow);
    }

    private sealed class TestKeyPair(RSA privateKey, string publicPem) : IDisposable
    {
        public RSA PrivateKey { get; } = privateKey;

        public string PublicPem { get; } = publicPem;

        public void Dispose() => PrivateKey.Dispose();
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(root, "config");
        public string SessionsPath => Path.Combine(root, "sessions");
        public string AuditPath => Path.Combine(root, "audit");
        public string LogsPath => Path.Combine(root, "logs");
        public string CredentialsPath => Path.Combine(root, "credentials");
        public string SkillsPath => Path.Combine(root, "skills");

        public void EnsureCreated() => Directory.CreateDirectory(root);

        public string ResolveSkillPath(string path) => path;
    }
}
