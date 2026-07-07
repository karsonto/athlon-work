using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public void TryGetEncryptedValue_ReturnsOaepEncryptedJson_WhenSsoValidAndPublicKeyPresent()
    {
        using var keys = CreateKeyPair();
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigPath);
        File.WriteAllText(
            Path.Combine(paths.ConfigPath, SsoEenoEnvironment.PublicKeyFileName),
            keys.PublicPem);

        var session = CreateValidSession("000974115");
        var store = new FakeSsoSessionStore(session);
        var settings = new AppSettings { Sso = { Enabled = true } };

        var encrypted = SsoEenoEnvironment.TryGetEncryptedValue(settings, store, paths);

        Assert.False(string.IsNullOrWhiteSpace(encrypted));

        var cipher = Convert.FromBase64String(encrypted!);
        var json = Encoding.UTF8.GetString(
            keys.PrivateKey.Decrypt(cipher, RSAEncryptionPadding.OaepSHA256)
        );

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("000974115", root.GetProperty("user_id").GetString());
        Assert.Equal("refresh", root.GetProperty("token_type").GetString());

        var jti = root.GetProperty("jti").GetString();
        Assert.NotNull(jti);
        Assert.InRange(jti.Length, 20, 30);

        Assert.Equal(session.ExpiresAt.UtcDateTime.ToString("o"),
            root.GetProperty("expires_at").GetString());
        Assert.Equal(session.LoggedInAt.UtcDateTime.ToString("o"),
            root.GetProperty("issued_at").GetString());
    }

    [Fact]
    public void TryApply_SetsMcpRefreshTokenEnvironmentVariable()
    {
        using var keys = CreateKeyPair();
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigPath);
        File.WriteAllText(
            Path.Combine(paths.ConfigPath, SsoEenoEnvironment.PublicKeyFileName),
            keys.PublicPem);

        var session = CreateValidSession("000974115");
        var store = new FakeSsoSessionStore(session);
        var settings = new AppSettings { Sso = { Enabled = true } };

        SsoEenoEnvironment.TestOverride = () => SsoEenoEnvironment.TryGetEncryptedValue(settings, store, paths);

        var startInfo = new ProcessStartInfo("test.exe");
        SsoEenoEnvironment.TryApply(startInfo);

        Assert.True(startInfo.Environment.ContainsKey(SsoEenoEnvironment.EnvVarName));
        Assert.Equal("MCP_REFRESH_TOKEN", SsoEenoEnvironment.EnvVarName);

        SsoEenoEnvironment.TestOverride = null;
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
