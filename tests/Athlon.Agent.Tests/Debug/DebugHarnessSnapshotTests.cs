using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Tests.Debug;

public sealed class DebugHarnessSnapshotTests
{
    [Fact]
    public void TryParseMode_ParsesDebug()
    {
        Assert.True(SessionHarnessSnapshot.TryParseMode("debug", out var mode));
        Assert.Equal(SessionAgentMode.Debug, mode);
    }

    [Fact]
    public void ToPersistedMode_RoundTripsDebug()
    {
        var snapshot = new SessionHarnessSnapshot(SessionAgentMode.Debug);
        Assert.Equal("debug", snapshot.ToPersistedMode());
        Assert.True(SessionHarnessSnapshot.TryParseMode(snapshot.ToPersistedMode(), out var parsed));
        Assert.Equal(SessionAgentMode.Debug, parsed);
    }

    [Fact]
    public void FromPersisted_ReadsDebugMode()
    {
        var snapshot = SessionHarnessSnapshot.FromPersisted(new SessionHarnessFile { Mode = "debug" });
        Assert.Equal(SessionAgentMode.Debug, snapshot.Mode);
    }
}
