using System.Runtime.Versioning;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Sso;

namespace Athlon.Agent.Tests;

public sealed class ImpSsoResponseParserTests
{
    [Fact]
    public void Parse_ReturnsValid_WhenUseridAndTimeoutRemainingPresent()
    {
        const string json = """
            {
              "userid": "000974115",
              "ename": "Zhang San",
              "timeoutRemaining": "3600000",
              "retcode": "0",
              "roleUserRelNum": "1"
            }
            """;

        var parsed = ImpSsoResponseParser.Parse(json);

        Assert.Equal(ImpSsoCheckStatus.Valid, parsed.Status);
        Assert.Equal("000974115", parsed.UserId);
        Assert.Equal("Zhang San", parsed.DisplayName);
    }

    [Fact]
    public void Parse_UsesUseridAsDisplayName_WhenEnameMissing()
    {
        const string json = """
            {
              "userid": "000974115",
              "timeoutRemaining": "3600000",
              "roleUserRelNum": "1"
            }
            """;

        var parsed = ImpSsoResponseParser.Parse(json);

        Assert.Equal(ImpSsoCheckStatus.Valid, parsed.Status);
        Assert.Equal("000974115", parsed.DisplayName);
    }

    [Fact]
    public void Parse_ReturnsReLoginRequired_WhenRetcodeIsOne()
    {
        const string json = """
            {
              "retcode": "1",
              "retmsg": "session expired"
            }
            """;

        var parsed = ImpSsoResponseParser.Parse(json);

        Assert.Equal(ImpSsoCheckStatus.ReLoginRequired, parsed.Status);
    }

    [Fact]
    public void Parse_ReturnsNoRole_WhenRoleUserRelNumIsZero()
    {
        const string json = """
            {
              "userid": "000974115",
              "timeoutRemaining": "3600000",
              "roleUserRelNum": "0"
            }
            """;

        var parsed = ImpSsoResponseParser.Parse(json);

        Assert.Equal(ImpSsoCheckStatus.NoRole, parsed.Status);
    }

    [Fact]
    public void Parse_ReturnsLoginRequired_WhenHtmlLoginPageReturned()
    {
        const string html = """
            <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Strict//EN"
              "https://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd">
            <html></html>
            """;

        var parsed = ImpSsoResponseParser.Parse(html);

        Assert.Equal(ImpSsoCheckStatus.LoginRequired, parsed.Status);
    }
}

public sealed class ImpSsoAuthServiceMappingTests
{
    [Fact]
    public void BuildImpLoginUrl_UsesMsgAndLoginHashFormat()
    {
        using var http = new HttpClient();
        var service = new ImpSsoAuthService(http);
        var settings = new SsoSettings
        {
            ImpDomain = "imp.icbcasiauat.com",
            AppId = "252",
            Msg = "123456789"
        };

        var url = service.BuildImpLoginUrl(settings);

        Assert.Equal(
            "https://imp.icbcasiauat.com/icbcasia/imp/index.html?toLogin=true&appId=252&msg=123456789#/login",
            url);
    }

    [Fact]
    public void MapParsedResult_SetsOneDayExpiryFromSettings()
    {
        var settings = new SsoSettings { SessionValidityHours = 24 };
        var parsed = ImpSsoParsedResponse.Valid("000974115", "Zhang San");
        var before = DateTimeOffset.UtcNow;

        var result = ImpSsoAuthService.MapParsedResult(parsed, "token-abc", settings);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Session);
        Assert.Equal("token-abc", result.Session!.SsoToken);
        Assert.Equal("Zhang San", result.Session.DisplayName);
        Assert.True(result.Session.LoggedInAt >= before);
        Assert.Equal(
            result.Session.LoggedInAt.AddHours(24),
            result.Session.ExpiresAt);
    }
}

public sealed class ImpSsoSessionExpiryTests
{
    [Fact]
    public void IsExpired_ReturnsTrue_WhenUtcNowPastExpiresAt()
    {
        var session = new ImpSsoSession
        {
            SsoToken = "token",
            UserId = "u1",
            DisplayName = "User",
            LoggedInAt = DateTimeOffset.UtcNow.AddHours(-25),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        Assert.True(session.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsExpired_ReturnsFalse_WhenWithinValidityWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new ImpSsoSession
        {
            SsoToken = "token",
            UserId = "u1",
            DisplayName = "User",
            LoggedInAt = now,
            ExpiresAt = now.AddHours(24)
        };

        Assert.False(session.IsExpired(now.AddHours(23)));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ImpSsoSessionStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-sso-test-" + Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();
        var store = new ImpSsoSessionStore(paths);
        var session = new ImpSsoSession
        {
            SsoToken = "secret-token",
            UserId = "000974115",
            DisplayName = "Zhang San",
            LoggedInAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };

        store.SaveSession(session);
        var loaded = store.GetCachedSession();

        Assert.NotNull(loaded);
        Assert.Equal(session.SsoToken, loaded!.SsoToken);
        Assert.Equal(session.DisplayName, loaded.DisplayName);
        Assert.False(store.IsExpired(loaded));

        store.Clear();
        Assert.Null(store.GetCachedSession());

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
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

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(AuditPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CredentialsPath);
            Directory.CreateDirectory(SkillsPath);
        }

        public string ResolveSkillPath(string path) => path;
    }
}
