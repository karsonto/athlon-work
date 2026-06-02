using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class AgentTurnSettingsTests
{
    [Theory]
    [InlineData(null, 30, true)]
    [InlineData(0, 0, false)]
    [InlineData(-5, 0, false)]
    [InlineData(30, 30, true)]
    [InlineData(999, 180, true)]
    public void ResolveTurnTimeout_ClampsAsExpected(int? timeoutMinutes, int expectedMinutes, bool hasTimeout)
    {
        AgentTurnSettings? settings = timeoutMinutes is null
            ? null
            : new AgentTurnSettings { TimeoutMinutes = timeoutMinutes.Value };

        Assert.Equal(hasTimeout, settings.HasTurnTimeout());
        Assert.Equal(expectedMinutes, settings.ResolveTurnTimeoutMinutes());

        var timeout = settings.ResolveTurnTimeout();
        if (hasTimeout)
        {
            Assert.NotNull(timeout);
            Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), timeout);
        }
        else
        {
            Assert.Null(timeout);
        }
    }

    [Fact]
    public void DefaultSettings_UsesThirtyMinutes()
    {
        var settings = new AgentTurnSettings();
        Assert.True(settings.HasTurnTimeout());
        Assert.Equal(30, settings.ResolveTurnTimeoutMinutes());
        Assert.Equal(TimeSpan.FromMinutes(30), settings.ResolveTurnTimeout());
    }
}
