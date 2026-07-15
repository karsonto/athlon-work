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
    public void EnrichSessionWithMcpToken_ReturnsUnchanged_WhenPublicKeyMissing()
    {
        var paths = CreatePaths();
        var session = CreateValidSession();

        var enriched = SsoEenoEnvironment.EnrichSessionWithMcpToken(session, paths);

        Assert.Same(session, enriched);
        Assert.Null(enriched.Jti);
        Assert.Null(enriched.McpRefreshToken);
    }

    [Fact]
    public void EnrichSessionWithMcpToken_ReturnsOaepEncryptedJson_WhenPublicKeyPresent()
    {
        using var keys = CreateKeyPair();
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigPath);
        File.WriteAllText(
            Path.Combine(paths.ConfigPath, SsoEenoEnvironment.PublicKeyFileName),
            keys.PublicPem);

        var session = CreateValidSession("000974115");

        var enriched = SsoEenoEnvironment.EnrichSessionWithMcpToken(session, paths);

        Assert.False(string.IsNullOrWhiteSpace(enriched.Jti));
        Assert.False(string.IsNullOrWhiteSpace(enriched.McpRefreshToken));
        Assert.InRange(enriched.Jti!.Length, 20, 30);

        var cipher = Convert.FromBase64String(enriched.McpRefreshToken!);
        var json = Encoding.UTF8.GetString(
            keys.PrivateKey.Decrypt(cipher, RSAEncryptionPadding.OaepSHA256)
        );

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("000974115", root.GetProperty("user_id").GetString());
        Assert.Equal("refresh", root.GetProperty("token_type").GetString());
        Assert.Equal(enriched.Jti, root.GetProperty("jti").GetString());
        Assert.Equal(session.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            root.GetProperty("expires_at").GetString());
        Assert.Equal(session.LoggedInAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            root.GetProperty("issued_at").GetString());
    }

    [Fact]
    public void EnrichSessionWithMcpToken_IsIdempotent_WhenTokenAlreadyPresent()
    {
        using var keys = CreateKeyPair();
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigPath);
        File.WriteAllText(
            Path.Combine(paths.ConfigPath, SsoEenoEnvironment.PublicKeyFileName),
            keys.PublicPem);

        var session = CreateValidSession("000974115");
        var enriched = SsoEenoEnvironment.EnrichSessionWithMcpToken(session, paths);
        var enrichedAgain = SsoEenoEnvironment.EnrichSessionWithMcpToken(enriched, paths);

        Assert.Same(enriched, enrichedAgain);
    }

    [Fact]
    public void TryGetStoredEncryptedValue_ReturnsNull_WhenSsoDisabled()
    {
        var store = new FakeSsoSessionStore(CreateValidSession(withToken: true));
        var settings = new AppSettings { Sso = { Enabled = false } };

        var encrypted = SsoEenoEnvironment.TryGetStoredEncryptedValue(settings, store);

        Assert.Null(encrypted);
    }

    [Fact]
    public void TryGetStoredEncryptedValue_ReturnsNull_WhenSessionExpired()
    {
        var store = new FakeSsoSessionStore(CreateExpiredSession(withToken: true));
        var settings = new AppSettings { Sso = { Enabled = true } };

        var encrypted = SsoEenoEnvironment.TryGetStoredEncryptedValue(settings, store);

        Assert.Null(encrypted);
    }

    [Fact]
    public void TryGetStoredEncryptedValue_ReturnsNull_WhenTokenMissing()
    {
        var store = new FakeSsoSessionStore(CreateValidSession());
        var settings = new AppSettings { Sso = { Enabled = true } };

        var encrypted = SsoEenoEnvironment.TryGetStoredEncryptedValue(settings, store);

        Assert.Null(encrypted);
    }

    [Fact]
    public void TryGetStoredEncryptedValue_ReturnsPersistedToken_WhenPresent()
    {
        const string token = "dGVzdC10b2tlbg==";
        var store = new FakeSsoSessionStore(CreateValidSession(withToken: true, mcpRefreshToken: token));
        var settings = new AppSettings { Sso = { Enabled = true } };

        var encrypted = SsoEenoEnvironment.TryGetStoredEncryptedValue(settings, store);

        Assert.Equal(token, encrypted);
    }

    [Fact]
    public void TryApply_SetsMcpRefreshTokenEnvironmentVariable()
    {
        const string token = "dGVzdC10b2tlbg==";
        var store = new FakeSsoSessionStore(CreateValidSession(withToken: true, mcpRefreshToken: token));
        var settings = new AppSettings { Sso = { Enabled = true } };

        SsoEenoEnvironment.TestOverride = () => SsoEenoEnvironment.TryGetStoredEncryptedValue(settings, store);

        var startInfo = new ProcessStartInfo("test.exe");
        SsoEenoEnvironment.TryApply(startInfo);

        Assert.Equal(token, startInfo.Environment[SsoEenoEnvironment.EnvVarName]);

        SsoEenoEnvironment.TestOverride = null;
    }

    [Fact]
    public void TryApply_SetsMcpRefreshTokenOnEnvironmentDictionary()
    {
        const string token = "dGVzdC1kaWN0LXRva2Vu";
        var store = new FakeSsoSessionStore(CreateValidSession(withToken: true, mcpRefreshToken: token));
        var settings = new AppSettings { Sso = { Enabled = true } };

        SsoEenoEnvironment.TestOverride = () => SsoEenoEnvironment.TryGetStoredEncryptedValue(settings, store);
        try
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            SsoEenoEnvironment.TryApply(env);

            Assert.Equal(token, env[SsoEenoEnvironment.EnvVarName]);
        }
        finally
        {
            SsoEenoEnvironment.TestOverride = null;
        }
    }

    [Fact]
    public void WithStdioSsoEnvironment_InjectsTokenForStdioServers()
    {
        const string token = "dGVzdC1tY3Atc3RkaW8=";
        SsoEenoEnvironment.TestOverride = () => token;
        try
        {
            var server = new McpServerSettings
            {
                Name = "demo",
                TransportType = "stdio",
                Command = "npx",
                Env = { ["EXISTING"] = "1" }
            };

            var enriched = McpRegistry.WithStdioSsoEnvironment(server);

            Assert.NotSame(server, enriched);
            Assert.Equal("1", enriched.Env["EXISTING"]);
            Assert.Equal(token, enriched.Env[SsoEenoEnvironment.EnvVarName]);
            Assert.False(server.Env.ContainsKey(SsoEenoEnvironment.EnvVarName));
        }
        finally
        {
            SsoEenoEnvironment.TestOverride = null;
        }
    }

    [Fact]
    public void WithStdioSsoEnvironment_SkipsHttpServers()
    {
        SsoEenoEnvironment.TestOverride = () => "should-not-apply";
        try
        {
            var server = new McpServerSettings
            {
                Name = "remote",
                TransportType = "http",
                Url = "https://example.com/mcp"
            };

            var enriched = McpRegistry.WithStdioSsoEnvironment(server);

            Assert.Same(server, enriched);
            Assert.False(enriched.Env.ContainsKey(SsoEenoEnvironment.EnvVarName));
        }
        finally
        {
            SsoEenoEnvironment.TestOverride = null;
        }
    }

    [Fact]
    public void EnrichSessionWithMcpToken_PersistsThroughSessionStore()
    {
        using var keys = CreateKeyPair();
        var paths = CreatePaths();
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.ConfigPath);
        File.WriteAllText(
            Path.Combine(paths.ConfigPath, SsoEenoEnvironment.PublicKeyFileName),
            keys.PublicPem);

        var session = CreateValidSession("000974115");
        var enriched = SsoEenoEnvironment.EnrichSessionWithMcpToken(session, paths);

        var store = new ImpSsoSessionStore(paths);
        store.SaveSession(enriched);

        var loaded = store.GetCachedSession();
        Assert.NotNull(loaded);
        Assert.Equal(enriched.Jti, loaded!.Jti);
        Assert.Equal(enriched.McpRefreshToken, loaded.McpRefreshToken);
    }

    private static ImpSsoSession CreateValidSession(
        string userId = "000974115",
        bool withToken = false,
        string? mcpRefreshToken = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ImpSsoSession
        {
            SsoToken = "token",
            UserId = userId,
            DisplayName = "Test User",
            LoggedInAt = now,
            ExpiresAt = now.AddHours(24),
            Jti = withToken ? "fixed-jti-value" : null,
            McpRefreshToken = withToken ? mcpRefreshToken ?? "dGVzdC10b2tlbg==" : null
        };
    }

    private static ImpSsoSession CreateExpiredSession(bool withToken = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new ImpSsoSession
        {
            SsoToken = "token",
            UserId = "000974115",
            DisplayName = "Test User",
            LoggedInAt = now.AddHours(-25),
            ExpiresAt = now.AddHours(-1),
            Jti = withToken ? "fixed-jti-value" : null,
            McpRefreshToken = withToken ? "dGVzdC10b2tlbg==" : null
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
