using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class AgentTurnSettingsTests
{
    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(999, 180)]
    public void ResolveTurnTimeoutMinutes_ClampsToExpected(int? timeoutMinutes, int expected)
    {
        AgentTurnSettings? settings = timeoutMinutes is null
            ? null
            : new AgentTurnSettings { TimeoutMinutes = timeoutMinutes.Value };

        Assert.Equal(expected, settings.ResolveTurnTimeoutMinutes());
        Assert.Equal(TimeSpan.FromMinutes(expected), settings.ResolveTurnTimeout());
    }

    [Fact]
    public void DefaultSettings_UsesThirtyMinutes()
    {
        var settings = new AgentTurnSettings();
        Assert.Equal(30, settings.ResolveTurnTimeoutMinutes());
    }
}
