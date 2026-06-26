using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Sso;

namespace Athlon.Agent.Tests;

public sealed class CurrentSsoUserContextTests
{
    [Fact]
    public void DisplayName_returns_null_when_sso_disabled()
    {
        var settings = new AppSettings { Sso = { Enabled = false } };
        var store = new ImpSsoSessionStore(new AppPathProvider());
        var context = new CurrentSsoUserContext(settings, store);

        Assert.Null(context.DisplayName);
    }

    [Fact]
    public void DisplayName_returns_trimmed_name_when_session_valid()
    {
        var settings = new AppSettings { Sso = { Enabled = true } };
        var paths = new AppPathProvider();
        var store = new ImpSsoSessionStore(paths);
        store.SaveSession(new ImpSsoSession
        {
            SsoToken = "token",
            UserId = "u1",
            DisplayName = "  Zhang San  ",
            LoggedInAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        var context = new CurrentSsoUserContext(settings, store);

        Assert.Equal("Zhang San", context.DisplayName);
    }
}
