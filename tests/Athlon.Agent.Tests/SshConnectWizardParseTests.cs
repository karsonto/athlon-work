using Athlon.Agent.App.Windows;

namespace Athlon.Agent.Tests;

public sealed class SshConnectWizardParseTests
{
    [Theory]
    [InlineData("user@host", "user", "host", 22)]
    [InlineData("user@host:2222", "user", "host", 2222)]
    [InlineData("host.example.com", "", "host.example.com", 22)]
    [InlineData("host:2200", "", "host", 2200)]
    public void TryParseHost_ParsesUserHostPort(string raw, string user, string host, int port)
    {
        Assert.True(SshConnectWizardWindow.TryParseHost(raw, out var username, out var parsedHost, out var parsedPort, out _));
        Assert.Equal(user, username);
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Fact]
    public void TryParseHost_RejectsEmpty()
    {
        Assert.False(SshConnectWizardWindow.TryParseHost("  ", out _, out _, out _, out _));
    }
}
